﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Analyzer.Utilities;
using Analyzer.Utilities.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis.DataFlow;
using Microsoft.CodeAnalysis.FlowAnalysis.DataFlow.DisposeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis.DataFlow.PointsToAnalysis;

namespace Microsoft.NetCore.Analyzers.Runtime
{
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public sealed class DisposeObjectsBeforeLosingScope : DiagnosticAnalyzer
    {
        internal const string RuleId = "CA2000";

        private static readonly LocalizableString s_localizableTitle = new LocalizableResourceString(nameof(SystemRuntimeAnalyzersResources.DisposeObjectsBeforeLosingScopeTitle), SystemRuntimeAnalyzersResources.ResourceManager, typeof(SystemRuntimeAnalyzersResources));
        private static readonly LocalizableString s_localizableNotDisposedMessage = new LocalizableResourceString(nameof(SystemRuntimeAnalyzersResources.DisposeObjectsBeforeLosingScopeNotDisposedMessage), SystemRuntimeAnalyzersResources.ResourceManager, typeof(SystemRuntimeAnalyzersResources));
        private static readonly LocalizableString s_localizableMayBeDisposedMessage = new LocalizableResourceString(nameof(SystemRuntimeAnalyzersResources.DisposeObjectsBeforeLosingScopeMayBeDisposedMessage), SystemRuntimeAnalyzersResources.ResourceManager, typeof(SystemRuntimeAnalyzersResources));
        private static readonly LocalizableString s_localizableNotDisposedOnExceptionPathsMessage = new LocalizableResourceString(nameof(SystemRuntimeAnalyzersResources.DisposeObjectsBeforeLosingScopeNotDisposedOnExceptionPathsMessage), SystemRuntimeAnalyzersResources.ResourceManager, typeof(SystemRuntimeAnalyzersResources));
        private static readonly LocalizableString s_localizableMayBeDisposedOnExceptionPathsMessage = new LocalizableResourceString(nameof(SystemRuntimeAnalyzersResources.DisposeObjectsBeforeLosingScopeMayBeDisposedOnExceptionPathsMessage), SystemRuntimeAnalyzersResources.ResourceManager, typeof(SystemRuntimeAnalyzersResources));
        private static readonly LocalizableString s_localizableDescription = new LocalizableResourceString(nameof(SystemRuntimeAnalyzersResources.DisposeObjectsBeforeLosingScopeDescription), SystemRuntimeAnalyzersResources.ResourceManager, typeof(SystemRuntimeAnalyzersResources));

        internal static DiagnosticDescriptor NotDisposedRule = new DiagnosticDescriptor(RuleId,
                                                                                        s_localizableTitle,
                                                                                        s_localizableNotDisposedMessage,
                                                                                        DiagnosticCategory.Reliability,
                                                                                        DiagnosticHelpers.DefaultDiagnosticSeverity,
                                                                                        isEnabledByDefault: DiagnosticHelpers.EnabledByDefaultIfNotBuildingVSIX,
                                                                                        description: s_localizableDescription,
                                                                                        helpLinkUri: "https://docs.microsoft.com/visualstudio/code-quality/ca2000-dispose-objects-before-losing-scope",
                                                                                        customTags: FxCopWellKnownDiagnosticTags.PortedFxCopDataflowRule);

        internal static DiagnosticDescriptor MayBeDisposedRule = new DiagnosticDescriptor(RuleId,
                                                                                          s_localizableTitle,
                                                                                          s_localizableMayBeDisposedMessage,
                                                                                          DiagnosticCategory.Reliability,
                                                                                          DiagnosticHelpers.DefaultDiagnosticSeverity,
                                                                                          isEnabledByDefault: DiagnosticHelpers.EnabledByDefaultIfNotBuildingVSIX,
                                                                                          description: s_localizableDescription,
                                                                                          helpLinkUri: "https://docs.microsoft.com/visualstudio/code-quality/ca2000-dispose-objects-before-losing-scope",
                                                                                          customTags: FxCopWellKnownDiagnosticTags.PortedFxCopDataflowRule);

        internal static DiagnosticDescriptor NotDisposedOnExceptionPathsRule = new DiagnosticDescriptor(RuleId,
                                                                                                        s_localizableTitle,
                                                                                                        s_localizableNotDisposedOnExceptionPathsMessage,
                                                                                                        DiagnosticCategory.Reliability,
                                                                                                        DiagnosticHelpers.DefaultDiagnosticSeverity,
                                                                                                        isEnabledByDefault: DiagnosticHelpers.EnabledByDefaultIfNotBuildingVSIX,
                                                                                                        description: s_localizableDescription,
                                                                                                        helpLinkUri: "https://docs.microsoft.com/visualstudio/code-quality/ca2000-dispose-objects-before-losing-scope",
                                                                                                        customTags: FxCopWellKnownDiagnosticTags.PortedFxCopDataflowRule);

        internal static DiagnosticDescriptor MayBeDisposedOnExceptionPathsRule = new DiagnosticDescriptor(RuleId,
                                                                                                          s_localizableTitle,
                                                                                                          s_localizableMayBeDisposedOnExceptionPathsMessage,
                                                                                                          DiagnosticCategory.Reliability,
                                                                                                          DiagnosticHelpers.DefaultDiagnosticSeverity,
                                                                                                          isEnabledByDefault: DiagnosticHelpers.EnabledByDefaultIfNotBuildingVSIX,
                                                                                                          description: s_localizableDescription,
                                                                                                          helpLinkUri: "https://docs.microsoft.com/visualstudio/code-quality/ca2000-dispose-objects-before-losing-scope",
                                                                                                          customTags: FxCopWellKnownDiagnosticTags.PortedFxCopDataflowRule);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(NotDisposedRule, MayBeDisposedRule, NotDisposedOnExceptionPathsRule, MayBeDisposedOnExceptionPathsRule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(compilationContext =>
            {
                if (!DisposeAnalysisHelper.TryGetOrCreate(compilationContext.Compilation, out DisposeAnalysisHelper disposeAnalysisHelper))
                {
                    return;
                }

                var reportedLocations = new ConcurrentDictionary<Location, bool>();
                compilationContext.RegisterOperationBlockAction(operationBlockContext =>
                {
                    if (!(operationBlockContext.OwningSymbol is IMethodSymbol containingMethod) ||
                        !disposeAnalysisHelper.HasAnyDisposableCreationDescendant(operationBlockContext.OperationBlocks, containingMethod))
                    {
                        return;
                    }

                    var disposeAnalysisKind = operationBlockContext.Options.GetDisposeAnalysisKindOption(NotDisposedOnExceptionPathsRule, DisposeAnalysisKind.AllPaths, operationBlockContext.CancellationToken);
                    var trackExceptionPaths = disposeAnalysisKind.AreExceptionPathsEnabled();

                    if (disposeAnalysisHelper.TryGetOrComputeResult(operationBlockContext.OperationBlocks, containingMethod,
                        operationBlockContext.Options, NotDisposedRule, trackInstanceFields: false, trackExceptionPaths,
                        operationBlockContext.CancellationToken, out var disposeAnalysisResult, out var pointsToAnalysisResult))
                    {
                        var notDisposedDiagnostics = ArrayBuilder<Diagnostic>.GetInstance();
                        var mayBeNotDisposedDiagnostics = ArrayBuilder<Diagnostic>.GetInstance();
                        try
                        {
                            // Compute diagnostics for undisposed objects at exit block for non-exceptional exit paths.
                            var exitBlock = disposeAnalysisResult.ControlFlowGraph.GetExit();
                            var disposeDataAtExit = disposeAnalysisResult.ExitBlockOutput.Data;
                            ComputeDiagnostics(disposeDataAtExit, containingMethod,
                                notDisposedDiagnostics, mayBeNotDisposedDiagnostics, disposeAnalysisResult, pointsToAnalysisResult,
                                disposeAnalysisKind, isDisposeDataForExceptionPaths: false);

                            if (trackExceptionPaths)
                            {
                                // Compute diagnostics for undisposed objects at handled exception exit paths.
                                var disposeDataAtHandledExceptionPaths = disposeAnalysisResult.ExceptionPathsExitBlockOutputOpt.Data;
                                ComputeDiagnostics(disposeDataAtHandledExceptionPaths, containingMethod,
                                    notDisposedDiagnostics, mayBeNotDisposedDiagnostics, disposeAnalysisResult, pointsToAnalysisResult,
                                    disposeAnalysisKind, isDisposeDataForExceptionPaths: true);

                                // Compute diagnostics for undisposed objects at unhandled exception exit paths, if any.
                                var disposeDataAtUnhandledExceptionPaths = disposeAnalysisResult.MergedStateForUnhandledThrowOperationsOpt?.Data;
                                if (disposeDataAtUnhandledExceptionPaths != null)
                                {
                                    ComputeDiagnostics(disposeDataAtUnhandledExceptionPaths, containingMethod,
                                        notDisposedDiagnostics, mayBeNotDisposedDiagnostics, disposeAnalysisResult, pointsToAnalysisResult,
                                        disposeAnalysisKind, isDisposeDataForExceptionPaths: true);
                                }
                            }

                            // Report diagnostics preferring *not* disposed diagnostics over may be not disposed diagnostics
                            // and avoiding duplicates.
                            foreach (var diagnostic in notDisposedDiagnostics.Concat(mayBeNotDisposedDiagnostics))
                            {
                                if (reportedLocations.TryAdd(diagnostic.Location, true))
                                {
                                    operationBlockContext.ReportDiagnostic(diagnostic);
                                }
                            }
                        }
                        finally
                        {
                            notDisposedDiagnostics.Free();
                            mayBeNotDisposedDiagnostics.Free();
                        }
                    }
                });
            });
        }

        private static void ComputeDiagnostics(
            ImmutableDictionary<AbstractLocation, DisposeAbstractValue> disposeData,
            IMethodSymbol containingMethod,
            ArrayBuilder<Diagnostic> notDisposedDiagnostics,
            ArrayBuilder<Diagnostic> mayBeNotDisposedDiagnostics,
            DisposeAnalysisResult disposeAnalysisResult,
            PointsToAnalysisResult pointsToAnalysisResult,
            DisposeAnalysisKind disposeAnalysisKind,
            bool isDisposeDataForExceptionPaths)
        {
            foreach (var kvp in disposeData)
            {
                AbstractLocation location = kvp.Key;
                DisposeAbstractValue disposeValue = kvp.Value;
                if (disposeValue.Kind == DisposeAbstractValueKind.NotDisposable ||
                    location.CreationOpt == null)
                {
                    continue;
                }

                var isNotDisposed = disposeValue.Kind == DisposeAbstractValueKind.NotDisposed ||
                    (disposeValue.DisposingOrEscapingOperations.Count > 0 &&
                     disposeValue.DisposingOrEscapingOperations.All(d => d.IsInsideCatchRegion(disposeAnalysisResult.ControlFlowGraph)));
                var isMayBeNotDisposed = !isNotDisposed && (disposeValue.Kind == DisposeAbstractValueKind.MaybeDisposed || disposeValue.Kind == DisposeAbstractValueKind.NotDisposedOrEscaped);

                if (isNotDisposed ||
                    (isMayBeNotDisposed && disposeAnalysisKind.AreMayBeNotDisposedViolationsEnabled()))
                {
                    var syntax = location.GetNodeToReportDiagnostic(pointsToAnalysisResult);

                    // CA2000: In method '{0}', call System.IDisposable.Dispose on object created by '{1}' before all references to it are out of scope.
                    var rule = GetRule(isNotDisposed);
                    var arg1 = containingMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    var arg2 = syntax.ToString();
                    var diagnostic = syntax.CreateDiagnostic(rule, arg1, arg2);
                    if (isNotDisposed)
                    {
                        notDisposedDiagnostics.Add(diagnostic);
                    }
                    else
                    {
                        mayBeNotDisposedDiagnostics.Add(diagnostic);
                    }
                }
            }

            DiagnosticDescriptor GetRule(bool isNotDisposed)
            {
                if (isNotDisposed)
                {
                    return isDisposeDataForExceptionPaths ? NotDisposedOnExceptionPathsRule : NotDisposedRule;
                }
                else
                {
                    return isDisposeDataForExceptionPaths ? MayBeDisposedOnExceptionPathsRule : MayBeDisposedRule;
                }
            }
        }
    }
}