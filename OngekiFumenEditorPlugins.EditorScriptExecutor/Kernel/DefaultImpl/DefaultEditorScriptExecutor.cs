﻿using Caliburn.Micro;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CSharp;
using OngekiFumenEditor;
using OngekiFumenEditor.Modules.FumenVisualEditor.Base;
using OngekiFumenEditor.Modules.FumenVisualEditor.ViewModels;
using OngekiFumenEditor.Utils;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace OngekiFumenEditorPlugins.EditorScriptExecutor.Kernel.DefaultImpl
{
    [Export(typeof(IEditorScriptExecutor))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class DefaultEditorScriptExecutor : IEditorScriptExecutor
    {
        private readonly List<MetadataReference> assemblyReferenceList;
        private readonly CSharpCompilationOptions compilationOptions;
        private readonly CSharpParseOptions parserOption;
        private readonly Dictionary<string, Assembly> refAssembliesMap = new();

        public DefaultEditorScriptExecutor()
        {
            assemblyReferenceList = new List<MetadataReference>() {
                MetadataReference.CreateFromFile(typeof(CodeCompiler).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(App).Assembly.Location),
            };

            var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location);

            assemblyReferenceList.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "mscorlib.dll")));
            assemblyReferenceList.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.dll")));
            assemblyReferenceList.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Core.dll")));
            assemblyReferenceList.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")));

            compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, usings: new[]{
                "System",
                "System.IO",
                "System.Diagnostics"
            })
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithPlatform(Platform.AnyCpu);

            parserOption = CSharpParseOptions.Default.WithKind(SourceCodeKind.Script).WithLanguageVersion(LanguageVersion.Preview);

            AppDomain.CurrentDomain.AssemblyResolve += AppDomain_AssemblyResolve;
        }

        public Task<BuildResult> Build(BuildParam param)
        {
            Log.LogDebug($"-------BEGIN SCRIPT BUILD--------");
            var overrideAssemblyLocations = assemblyReferenceList.ToList();
            foreach (var newLoc in param.AssemblyLocations)
                overrideAssemblyLocations.Add(MetadataReference.CreateFromFile(newLoc));

            var encoding = Encoding.UTF8;

            var assemblyName = Path.GetRandomFileName();
            var sourceCodePath = param.DisplayFileName ?? Path.ChangeExtension(assemblyName, "cs");

            var buffer = encoding.GetBytes(param.Script);
            var sourceText = SourceText.From(buffer, buffer.Length, encoding, canBeEmbedded: true);

            return Task.Run(() =>
            {
                var removeTriviaSyntaxs = new List<ReferenceDirectiveTriviaSyntax>();
                (bool, string) TryApplyReferenceDirective(ReferenceDirectiveTriviaSyntax syntax)
                {
                    if (syntax is null)
                        return (true, default);
                    var appendAsmFilePath = syntax.File.ValueText;
                    removeTriviaSyntaxs.Add(syntax);
                    if (!File.Exists(appendAsmFilePath))
                        return (false, $"Assembly file not found : {appendAsmFilePath}");
                    try
                    {
                        var metadataRef = MetadataReference.CreateFromFile(appendAsmFilePath);
                        overrideAssemblyLocations.Add(metadataRef);
                    }
                    catch (Exception e)
                    {
                        return (false, $"Assembly can't load : {appendAsmFilePath} , reason : {e.Message}");
                    }
                    TryCacheDLLAssembly(appendAsmFilePath);
                    return TryApplyReferenceDirective(syntax.GetNextDirective(x => x.Kind() == SyntaxKind.ReferenceDirectiveTrivia) as ReferenceDirectiveTriviaSyntax);
                }

                var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, parserOption, sourceCodePath);

                var rootNode = syntaxTree.GetRoot();

                (var isSuccess, var errMsg) = TryApplyReferenceDirective(rootNode.GetFirstDirective(x => x.Kind() == SyntaxKind.ReferenceDirectiveTrivia) as ReferenceDirectiveTriviaSyntax);
                if (!isSuccess)
                {
                    return new BuildResult()
                    {
                        Errors = new[]
                        {
                            Diagnostic.Create(new DiagnosticDescriptor("DP0000","",errMsg,"", DiagnosticSeverity.Error,true),default)
                        }
                    };
                }

                var removedRoot = rootNode.RemoveNodes(removeTriviaSyntaxs, SyntaxRemoveOptions.KeepNoTrivia);

                overrideAssemblyLocations.DistinctSelf();
                overrideAssemblyLocations.ForEach(x => Log.LogDebug($"asmLoc: {x.Display}"));

                var comp = CSharpCompilation.CreateScriptCompilation(
                    assemblyName,
                    removedRoot.SyntaxTree,
                    overrideAssemblyLocations,
                    compilationOptions
                );

                var symbolsName = Path.ChangeExtension(assemblyName, "pdb");

                var emitOptions = new EmitOptions(
                            debugInformationFormat: DebugInformationFormat.PortablePdb,
                            pdbFilePath: symbolsName);

                var embeddedTexts = new List<EmbeddedText>
                {
                    EmbeddedText.FromSource(sourceCodePath, sourceText),
                };

                using var peStream = new MemoryStream();
                using var pdbStream = new MemoryStream();

                var emitResult = comp.Emit(peStream, pdbStream, embeddedTexts: embeddedTexts, options: emitOptions);
                var diagnostics = emitResult.Diagnostics.ToArray();

                if (!emitResult.Success)
                    return new BuildResult(emitResult);

                var assembly = Assembly.Load(peStream.ToArray(), pdbStream.ToArray());

                var r = assembly.GetReferencedAssemblies();

                Log.LogDebug($"-------END SCRIPT BUILD--------");
                return new BuildResult(comp)
                {
                    Assembly = assembly,
                    EntryPoint = comp.GetEntryPoint(default),
                };
            });
        }

        private void TryCacheDLLAssembly(string appendAsmFilePath)
        {
            try
            {
                var asm = Assembly.LoadFrom(appendAsmFilePath);
                var name = asm.GetName().FullName;

                var loadedAsmList = AppDomain.CurrentDomain.GetAssemblies();
                if (loadedAsmList.Any(x => x.GetName().FullName == name))
                    return;

                refAssembliesMap[name] = asm;
            }
            catch
            {

            }
        }

        public async Task<ExecuteResult> Execute(BuildParam param, FumenVisualEditorViewModel targetEditor)
        {
            var buildResult = await Build(param);

            if (!buildResult.IsSuccess)
                return new(false, "无法编译脚本:" + Environment.NewLine + string.Join(Environment.NewLine, buildResult.Errors));

            var assembly = buildResult.Assembly;
            if (assembly is null)
                return new(false, "failed to generate assembly : " + Environment.NewLine + string.Join(Environment.NewLine, buildResult.Errors));

            var result = await Execute(buildResult, targetEditor);
            return result;
        }

        private Assembly AppDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            if (args.RequestingAssembly is not null)
                return null;

            refAssembliesMap.TryGetValue(args.Name, out var assembly);

            return assembly;
        }

        public async Task<ExecuteResult> Execute(BuildResult buildResult, FumenVisualEditorViewModel targetEditor)
        {
            var assembly = buildResult.Assembly;
            var ep = buildResult.EntryPoint;

            var epType = assembly.GetType($"{ep.ContainingNamespace.MetadataName}.{ep.ContainingType.MetadataName}");
            var epMethod = epType.GetMethod(ep.MetadataName);
            var name = $"{epType.GetTypeName()}.{epMethod.Name}()";

            Log.LogDebug($"Script endpoint : {name}");

            try
            {
                var func = epMethod.CreateDelegate(typeof(Func<object[], Task<object>>)) as Func<object[], Task<object>>;
                Log.LogDebug($"Script begin call : {name}");
                var obj = await func(new object[2]);
                Log.LogDebug($"Script end call : {name}");
                return new(true, null, obj);
            }
            catch (Exception e)
            {
                return new(false, e.Message);
            }
        }
    }
}
