﻿// Licensed to the .NET Foundation under one or more agreements. The .NET Foundation licenses this file to you under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServices.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.OperationProgress;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.ProjectSystem.Utilities;

namespace Microsoft.VisualStudio.ProjectSystem.LanguageServices
{
    internal partial class WorkspaceProjectContextHost
    {
        /// <summary>
        ///     Responsible for lifetime of a <see cref="IWorkspaceProjectContext"/> and applying changes to a
        ///     project to the context via the <see cref="IApplyChangesToWorkspaceContext"/> service.
        /// </summary>
        internal partial class WorkspaceProjectContextHostInstance : OnceInitializedOnceDisposedUnderLockAsync, IMultiLifetimeInstance
        {
            private readonly ConfiguredProject _project;
            private readonly IProjectSubscriptionService _projectSubscriptionService;
            private readonly IUnconfiguredProjectTasksService _tasksService;
            private readonly IWorkspaceProjectContextProvider _workspaceProjectContextProvider;
            private readonly IActiveEditorContextTracker _activeWorkspaceProjectContextTracker;
            private readonly IActiveConfiguredProjectProvider _activeConfiguredProjectProvider;
            private readonly ExportFactory<IApplyChangesToWorkspaceContext> _applyChangesToWorkspaceContextFactory;
            private readonly IDataProgressTrackerService _dataProgressTrackerService;
            private readonly ICommandLineArgumentsProvider _commandLineArgumentsProvider;

            private IDataProgressTrackerServiceRegistration? _evaluationProgressRegistration;
            private IDataProgressTrackerServiceRegistration? _projectBuildProgressRegistration;
            private DisposableBag? _disposables;
            private IWorkspaceProjectContextAccessor? _contextAccessor;
            private ExportLifetimeContext<IApplyChangesToWorkspaceContext>? _applyChangesToWorkspaceContext;

            public WorkspaceProjectContextHostInstance(ConfiguredProject project,
                                                       IProjectThreadingService threadingService,
                                                       IUnconfiguredProjectTasksService tasksService,
                                                       IProjectSubscriptionService projectSubscriptionService,
                                                       IWorkspaceProjectContextProvider workspaceProjectContextProvider,
                                                       IActiveEditorContextTracker activeWorkspaceProjectContextTracker,
                                                       IActiveConfiguredProjectProvider activeConfiguredProjectProvider,
                                                       ExportFactory<IApplyChangesToWorkspaceContext> applyChangesToWorkspaceContextFactory,
                                                       IDataProgressTrackerService dataProgressTrackerService,
                                                       ICommandLineArgumentsProvider commandLineArgumentsProvider)
                : base(threadingService.JoinableTaskContext)
            {
                _project = project;
                _projectSubscriptionService = projectSubscriptionService;
                _tasksService = tasksService;
                _workspaceProjectContextProvider = workspaceProjectContextProvider;
                _activeWorkspaceProjectContextTracker = activeWorkspaceProjectContextTracker;
                _activeConfiguredProjectProvider = activeConfiguredProjectProvider;
                _applyChangesToWorkspaceContextFactory = applyChangesToWorkspaceContextFactory;
                _dataProgressTrackerService = dataProgressTrackerService;
                _commandLineArgumentsProvider = commandLineArgumentsProvider;
            }

            public Task InitializeAsync()
            {
                return InitializeAsync(CancellationToken.None);
            }

            protected override async Task InitializeCoreAsync(CancellationToken cancellationToken)
            {
                _contextAccessor = await _workspaceProjectContextProvider.CreateProjectContextAsync(_project);

                if (_contextAccessor == null)
                    return;

                _activeWorkspaceProjectContextTracker.RegisterContext(_contextAccessor.ContextId);

                _applyChangesToWorkspaceContext = _applyChangesToWorkspaceContextFactory.CreateExport();
                _applyChangesToWorkspaceContext.Value.Initialize(_contextAccessor.Context);

                _evaluationProgressRegistration = _dataProgressTrackerService.RegisterForIntelliSense(this, _project, nameof(WorkspaceProjectContextHostInstance) + ".Evaluation");
                _projectBuildProgressRegistration = _dataProgressTrackerService.RegisterForIntelliSense(this, _project, nameof(WorkspaceProjectContextHostInstance) + ".ProjectBuild");

                StrongBox<ContextState?> lastEvaluationContextState = new();
                StrongBox<ContextState?> lastBuildContextState = new();

                _disposables = new DisposableBag
                {
                    _applyChangesToWorkspaceContext,
                    _evaluationProgressRegistration,
                    _projectBuildProgressRegistration,

                    ProjectDataSources.SyncLinkTo(
                        _activeConfiguredProjectProvider.ActiveConfiguredProjectBlock.SyncLinkOptions(),
                        _projectSubscriptionService.ProjectRuleSource.SourceBlock.SyncLinkOptions(GetProjectEvaluationOptions()),
                        _projectSubscriptionService.SourceItemsRuleSource.SourceBlock.SyncLinkOptions(),
                        target: DataflowBlockFactory.CreateActionBlock<IProjectVersionedValue<(ConfiguredProject, IProjectSubscriptionUpdate, IProjectSubscriptionUpdate)>>(
                            OnEvaluationUpdateAsync,
                            _project.UnconfiguredProject,
                            ProjectFaultSeverity.LimitedFunctionality),
                        linkOptions: DataflowOption.PropagateCompletion,
                        cancellationToken: cancellationToken),

                    ProjectDataSources.SyncLinkTo(
                        _activeConfiguredProjectProvider.ActiveConfiguredProjectBlock.SyncLinkOptions(),
                        _projectSubscriptionService.ProjectBuildRuleSource.SourceBlock.SyncLinkOptions(GetProjectBuildOptions()),
                        _commandLineArgumentsProvider.SourceBlock.SyncLinkOptions(),
                        target: DataflowBlockFactory.CreateActionBlock<IProjectVersionedValue<(ConfiguredProject, IProjectSubscriptionUpdate, CommandLineArgumentsSnapshot)>>(
                            OnBuildUpdateAsync,
                            _project.UnconfiguredProject,
                            ProjectFaultSeverity.LimitedFunctionality),
                        linkOptions: DataflowOption.PropagateCompletion,
                        cancellationToken: cancellationToken)
                };

                return;

                StandardRuleDataflowLinkOptions GetProjectEvaluationOptions()
                {
                    return DataflowOption.WithRuleNames(_applyChangesToWorkspaceContext.Value.GetProjectEvaluationRules());
                }

                StandardRuleDataflowLinkOptions GetProjectBuildOptions()
                {
                    return DataflowOption.WithRuleNames(_applyChangesToWorkspaceContext.Value.GetProjectBuildRules());
                }

                Task OnEvaluationUpdateAsync(IProjectVersionedValue<(ConfiguredProject ActiveConfiguredProject, IProjectSubscriptionUpdate ProjectUpdate, IProjectSubscriptionUpdate SourceItemsUpdate)> e)
                {
                    return OnProjectChangedAsync(
                        _evaluationProgressRegistration,
                        e.Value.ActiveConfiguredProject,
                        lastEvaluationContextState,
                        e.DataSourceVersions,
                        hasChange: () => e.Value.ProjectUpdate.ProjectChanges.HasChange() || e.Value.SourceItemsUpdate.ProjectChanges.HasChange(),
                        applyFunc: (contextState, token) => _applyChangesToWorkspaceContext.Value.ApplyProjectEvaluation(e.Derive(v => (v.ProjectUpdate, v.SourceItemsUpdate)), contextState, cancellationToken));
                }

                Task OnBuildUpdateAsync(IProjectVersionedValue<(ConfiguredProject ActiveConfiguredProject, IProjectSubscriptionUpdate BuildUpdate, CommandLineArgumentsSnapshot CommandLineArgumentsUpdate)> e)
                {
                    return OnProjectChangedAsync(
                        _projectBuildProgressRegistration,
                        e.Value.ActiveConfiguredProject,
                        lastBuildContextState,
                        e.DataSourceVersions,
                        hasChange: () => e.Value.BuildUpdate.ProjectChanges.HasChange() || e.Value.CommandLineArgumentsUpdate.IsChanged,
                        applyFunc: (contextState, token) => _applyChangesToWorkspaceContext.Value.ApplyProjectBuild(e.Derive(v => (v.BuildUpdate, v.CommandLineArgumentsUpdate)), contextState, cancellationToken));
                }
            }

            protected override async Task DisposeCoreUnderLockAsync(bool initialized)
            {
                if (initialized)
                {
                    _disposables?.Dispose();

                    if (_contextAccessor != null)
                    {
                        _activeWorkspaceProjectContextTracker.UnregisterContext(_contextAccessor.ContextId);

                        await _workspaceProjectContextProvider.ReleaseProjectContextAsync(_contextAccessor);
                    }
                }
            }

            public async Task OpenContextForWriteAsync(Func<IWorkspaceProjectContextAccessor, Task> action)
            {
                CheckForInitialized();

                try
                {
                    await ExecuteUnderLockAsync(_ => action(_contextAccessor), _tasksService.UnloadCancellationToken);
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == DisposalToken)
                {   // We treat cancellation because our instance was disposed differently from when the project is unloading.
                    // 
                    // The former indicates that the active configuration changed, and our ConfiguredProject is no longer 
                    // considered implicitly "active", we throw a different exceptions to let callers handle that.
                    throw new ActiveProjectConfigurationChangedException();
                }
            }

            public async Task<T> OpenContextForWriteAsync<T>(Func<IWorkspaceProjectContextAccessor, Task<T>> action)
            {
                CheckForInitialized();

                try
                {
                    return await ExecuteUnderLockAsync(_ => action(_contextAccessor), _tasksService.UnloadCancellationToken);
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == DisposalToken)
                {
                    throw new ActiveProjectConfigurationChangedException();
                }
            }

            internal Task OnProjectChangedAsync(
                IDataProgressTrackerServiceRegistration registration,
                ConfiguredProject activeConfiguredProject,
                StrongBox<ContextState?> lastContextState,
                IImmutableDictionary<NamedIdentity, IComparable> dataSourceVersions,
                Func<bool> hasChange,
                Action<ContextState, CancellationToken> applyFunc)
            {
                return ExecuteUnderLockAsync(ApplyProjectChangesUnderLockAsync, _tasksService.UnloadCancellationToken);

                Task ApplyProjectChangesUnderLockAsync(CancellationToken cancellationToken)
                {
                    // NOTE we cannot call CheckForInitialized here, as this method may be invoked during initialization
                    Assumes.NotNull(_contextAccessor);

                    (ContextState contextState, bool stateChanged) = GetContextState(lastContextState);

                    if (!stateChanged && !hasChange())
                    {
                        // No change since the last update. We must still update operation progress, but can skip creating a batch.
                        UpdateProgressRegistration();
                        return Task.CompletedTask;
                    }

                    return ApplyInBatchAsync();

                    async Task ApplyInBatchAsync()
                    {
                        IWorkspaceProjectContext context = _contextAccessor.Context;

                        context.StartBatch();

                        try
                        {
                            applyFunc(contextState, cancellationToken);
                        }
                        finally
                        {
                            await context.EndBatchAsync();

                            UpdateProgressRegistration();
                        }
                    }

                    (ContextState ContextState, bool StateChanged) GetContextState(StrongBox<ContextState?> lastContextState)
                    {
                        bool isActiveConfiguration = activeConfiguredProject == _project;
                        bool isActiveEditorContext = _activeWorkspaceProjectContextTracker.IsActiveEditorContext(_contextAccessor.ContextId);
                        
                        ContextState newState = new(isActiveEditorContext, isActiveConfiguration);

                        bool stateChanged = false;

                        if (lastContextState.Value is null ||
                            lastContextState.Value.Value.IsActiveConfiguration != newState.IsActiveConfiguration ||
                            lastContextState.Value.Value.IsActiveEditorContext != newState.IsActiveEditorContext)
                        {
                            // The state has changed
                            lastContextState.Value = newState;
                            stateChanged = true;
                        }

                        return (newState, stateChanged);
                    }

                    void UpdateProgressRegistration()
                    {
                        // Notify operation progress that we've now processed these versions of our input, if they are
                        // up-to-date with the latest version that produced, then we no longer considered "in progress".
                        registration.NotifyOutputDataCalculated(dataSourceVersions);
                    }
                }
            }

            [MemberNotNull(nameof(_contextAccessor))]
            private void CheckForInitialized()
            {
                // We should have been initialized by our 
                // owner before they called into us
                Assumes.True(IsInitialized);

                // If we failed to create a context, we treat it as a cancellation
                if (_contextAccessor == null)
                    throw new OperationCanceledException();
            }
        }
    }
}
