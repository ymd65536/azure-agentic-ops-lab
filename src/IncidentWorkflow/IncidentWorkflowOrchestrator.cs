using System.Diagnostics;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.Observability;
using AzureAgenticOps.RuleEvaluator;

namespace AzureAgenticOps.IncidentWorkflow;

/// <summary>
/// The final result of an incident workflow run.
/// </summary>
/// <param name="SchemaVersion">The contract schema version.</param>
/// <param name="IncidentId">The incident that was processed.</param>
/// <param name="WorkflowInstanceId">The workflow instance identifier.</param>
/// <param name="CorrelationId">The correlation identifier used across all operations.</param>
/// <param name="FinalState">The terminal state the workflow reached.</param>
/// <param name="Summary">A human-readable explanation of the terminal state.</param>
/// <param name="StateHistory">Every state the workflow passed through, in order.</param>
/// <param name="StartedAt">When the workflow started.</param>
/// <param name="CompletedAt">When the workflow reached its terminal state.</param>
public sealed record IncidentWorkflowResult(
    string SchemaVersion,
    string IncidentId,
    string WorkflowInstanceId,
    string CorrelationId,
    IncidentWorkflowState FinalState,
    string Summary,
    IReadOnlyList<IncidentWorkflowState> StateHistory,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

/// <summary>
/// The deterministic incident workflow orchestrator. The orchestrator, not the
/// agents, owns state transitions, activity ordering, retry boundaries,
/// escalation, human approval, execution, verification, rollback, and
/// termination. Every loop is bounded by <see cref="IncidentWorkflowOptions"/>
/// so the workflow always terminates safely, and every transition and outcome is
/// published as an <see cref="IncidentLifecycleEvent"/>. Lifecycle publishing
/// failures never block the remediation path.
/// </summary>
public sealed class IncidentWorkflowOrchestrator
{
    private const string ComponentName = "IncidentWorkflow";

    private readonly IIncidentWorkflowActivities _activities;
    private readonly IApprovalGate _approvalGate;
    private readonly ILifecycleEventPublisher _eventPublisher;
    private readonly IncidentWorkflowOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly AgenticOpsMetrics? _metrics;

    /// <summary>Initializes a new orchestrator.</summary>
    /// <param name="activities">The workflow activities.</param>
    /// <param name="approvalGate">The external human approval gate.</param>
    /// <param name="eventPublisher">The lifecycle event publisher.</param>
    /// <param name="options">The workflow options. Defaults to <see cref="IncidentWorkflowOptions.Default"/>.</param>
    /// <param name="timeProvider">The time provider. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="metrics">The optional metrics sink. When <c>null</c>, no metrics are emitted.</param>
    public IncidentWorkflowOrchestrator(
        IIncidentWorkflowActivities activities,
        IApprovalGate approvalGate,
        ILifecycleEventPublisher eventPublisher,
        IncidentWorkflowOptions? options = null,
        TimeProvider? timeProvider = null,
        AgenticOpsMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(approvalGate);
        ArgumentNullException.ThrowIfNull(eventPublisher);
        _activities = activities;
        _approvalGate = approvalGate;
        _eventPublisher = eventPublisher;
        _options = options ?? IncidentWorkflowOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metrics = metrics;
    }

    /// <summary>
    /// Runs the incident workflow to a terminal state.
    /// </summary>
    /// <param name="incident">The incident to process.</param>
    /// <param name="workflowInstanceId">The workflow instance identifier.</param>
    /// <param name="correlationId">The correlation identifier for observability.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The workflow result with the terminal state and full state history.</returns>
    public async Task<IncidentWorkflowResult> RunAsync(
        Incident incident,
        string workflowInstanceId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var run = new WorkflowRun(incident, workflowInstanceId, correlationId, _timeProvider.GetUtcNow());
        using Activity? workflowSpan = AgenticOpsActivitySource.StartSpan(SpanNames.WorkflowExecution, run.Correlation);
        _metrics?.RecordIncidentReceived();
        await EmitAsync(run, "IncidentReceived", outcome: null, details: null, cancellationToken).ConfigureAwait(false);

        await ExecuteLifecycleAsync(run, cancellationToken).ConfigureAwait(false);

        DateTimeOffset completedAt = _timeProvider.GetUtcNow();
        workflowSpan?.SetTag(ObservabilityTags.FinalState, run.State.ToString());
        if (run.State == IncidentWorkflowState.Resolved)
        {
            AgenticOpsActivitySource.RecordSuccess(workflowSpan, run.State.ToString());
            _metrics?.RecordIncidentResolved(completedAt - run.StartedAt);
        }
        else
        {
            AgenticOpsActivitySource.RecordFailure(workflowSpan, run.State.ToString());
            _metrics?.RecordIncidentFailed(run.State.ToString(), completedAt - run.StartedAt);
        }

        return new IncidentWorkflowResult(
            SchemaVersions.V1,
            incident.IncidentId,
            workflowInstanceId,
            correlationId,
            run.State,
            run.Summary,
            run.StateHistory,
            run.StartedAt,
            completedAt);
    }

    private async Task ExecuteLifecycleAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        // Received -> Classifying: collect initial evidence with bounded attempts.
        await TransitionAsync(run, IncidentWorkflowState.Classifying, cancellationToken).ConfigureAwait(false);
        if (!await TryCollectEvidenceAsync(run, cancellationToken).ConfigureAwait(false))
        {
            await FailAsync(run, "Evidence collection failed after the maximum number of attempts.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // Classifying -> RuleEvaluation: deterministic routing before any model call.
        await TransitionAsync(run, IncidentWorkflowState.RuleEvaluation, cancellationToken).ConfigureAwait(false);
        RuleEvaluationResult? ruleResult = await RunActivityAsync(
            run, "RuleEvaluation", 1,
            token => _activities.EvaluateRulesAsync(run.Incident, run.Evidence, run.CorrelationId, token),
            cancellationToken).ConfigureAwait(false);
        if (ruleResult is null)
        {
            await FailAsync(run, "Rule evaluation failed.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await EmitAsync(run, "RuleEvaluationCompleted", ruleResult.Classification.ToString(), new Dictionary<string, string>
        {
            ["classification"] = ruleResult.Classification.ToString(),
            ["matchedPattern"] = ruleResult.MatchedPatternName ?? string.Empty,
        }, cancellationToken).ConfigureAwait(false);

        // The rule-based handling is summarized deterministically so that Tier 1
        // always learns which incident was handled and what the rule-based path
        // actually did before the incident was shared with it.
        bool ruleAutoExecutionAllowed = false;
        ExecutionOutcome? ruleExecutionOutcome = null;
        VerificationOutcome? ruleVerificationOutcome = null;
        string escalationReason =
            "Rule evaluation did not produce a deterministic remediation for this incident.";

        // Rule fast path: a known pattern with an approved deterministic action
        // resolves the incident without any model call. Policy decides whether
        // the action may auto-execute; anything else escalates to Tier 1.
        if (ruleResult.Classification == IncidentClassification.Known &&
            ruleResult.RecommendedDisposition == AgentDisposition.Resolve &&
            !ruleResult.EscalateToTier2 &&
            ruleResult.ProposedActionType is not null)
        {
            RuleRemediationDecision? decision = await RunActivityAsync(
                run, "RuleRemediationPreparation", 1,
                token => _activities.PrepareRuleRemediationAsync(run.Incident, ruleResult, run.CorrelationId, token),
                cancellationToken).ConfigureAwait(false);

            await EmitAsync(run, "RuleRemediationPrepared",
                decision is { CanAutoExecute: true } ? "allowed" : "escalated",
                new Dictionary<string, string>
                {
                    ["actionType"] = ruleResult.ProposedActionType,
                    ["reason"] = decision?.Reason ?? "The rule remediation preparation activity failed.",
                }, cancellationToken).ConfigureAwait(false);

            escalationReason = decision?.Reason ?? "The rule remediation preparation activity failed.";

            if (decision is { CanAutoExecute: true, Action: not null })
            {
                ruleAutoExecutionAllowed = true;
                await TransitionAsync(run, IncidentWorkflowState.Executing, cancellationToken).ConfigureAwait(false);
                ExecutionResult? ruleExecution = await ExecuteWithRetryAsync(
                    run, decision.Action, approvalGranted: false,
                    _options.MaxExecutionAttemptsPerAction, cancellationToken).ConfigureAwait(false);
                ruleExecutionOutcome = ruleExecution?.Outcome;

                if (ruleExecution is not null && ruleExecution.Outcome is not ExecutionOutcome.Failed and not ExecutionOutcome.Rejected)
                {
                    await TransitionAsync(run, IncidentWorkflowState.Verifying, cancellationToken).ConfigureAwait(false);
                    VerificationResult? ruleVerification = await RunActivityAsync(
                        run, "Verification", 1,
                        token => _activities.VerifyTier1RemediationAsync(run.Incident, decision.Action, run.CorrelationId, token),
                        cancellationToken).ConfigureAwait(false);
                    ruleVerificationOutcome = ruleVerification?.Outcome;

                    await EmitAsync(run, "VerificationCompleted", ruleVerification?.Outcome.ToString() ?? "error", null, cancellationToken).ConfigureAwait(false);
                    if (ruleVerification is not null && ruleVerification.Outcome == VerificationOutcome.Passed)
                    {
                        await ResolveAsync(run,
                            $"Known pattern '{ruleResult.MatchedPatternName}' was remediated deterministically by rule evaluation and verification passed.",
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    _metrics?.RecordVerificationFailure(ruleVerification?.Outcome.ToString() ?? "error");
                }

                // Execution or verification did not succeed: escalate to Tier 1
                // investigation instead of retrying a rule remediation blindly.
                escalationReason = "The rule fast-path remediation did not produce a verified resolution.";
                await EmitAsync(run, "RuleRemediationEscalated", "tier1", new Dictionary<string, string>
                {
                    ["reason"] = escalationReason,
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        // Everything that the rule-based path could not resolve is shared with
        // Tier 1, together with a factual summary of the rule-based handling.
        var ruleHandling = new RuleHandlingSummary(
            SchemaVersions.V1,
            run.Incident.IncidentId,
            ruleResult.Classification,
            ruleResult.MatchedPatternName,
            ruleResult.Confidence,
            ruleResult.ProposedActionType,
            ruleAutoExecutionAllowed,
            ruleExecutionOutcome,
            ruleVerificationOutcome,
            escalationReason,
            ruleResult.ReasonSummary);

        await EmitAsync(run, "Tier1RuleHandoffShared", ruleHandling.Classification.ToString(), new Dictionary<string, string>
        {
            ["incidentTitle"] = run.Incident.Title,
            ["severity"] = run.Incident.Severity,
            ["affectedServices"] = string.Join(", ", run.Incident.AffectedServices),
            ["ruleClassification"] = ruleHandling.Classification.ToString(),
            ["ruleMatchedPattern"] = ruleHandling.MatchedPatternName ?? string.Empty,
            ["ruleProposedActionType"] = ruleHandling.ProposedActionType ?? string.Empty,
            ["ruleAutoExecutionAllowed"] = ruleHandling.AutoExecutionAllowed ? "true" : "false",
            ["ruleExecutionOutcome"] = ruleHandling.ExecutionOutcome?.ToString() ?? "none",
            ["ruleVerificationOutcome"] = ruleHandling.VerificationOutcome?.ToString() ?? "none",
            ["ruleReasonSummary"] = ruleHandling.RuleReasonSummary,
            ["escalationReason"] = ruleHandling.EscalationReason,
        }, cancellationToken).ConfigureAwait(false);

        // Tier 1 investigation loop, bounded by Tier 1 and evidence attempt counts.
        InvestigationResult? tier1Result = null;
        while (true)
        {
            if (run.State != IncidentWorkflowState.Tier1Investigation)
            {
                await TransitionAsync(run, IncidentWorkflowState.Tier1Investigation, cancellationToken).ConfigureAwait(false);
            }

            run.Tier1Attempts++;
            tier1Result = await RunActivityAsync(
                run, "Tier1Investigation", run.Tier1Attempts,
                token => _activities.RunTier1InvestigationAsync(run.Incident, run.Evidence, ruleHandling, run.CorrelationId, token),
                cancellationToken).ConfigureAwait(false);

            if (tier1Result is null)
            {
                if (run.Tier1Attempts >= _options.MaxTier1Attempts)
                {
                    await FailAsync(run, "Tier 1 investigation failed after the maximum number of attempts.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                continue;
            }

            await EmitAsync(run, "Tier1InvestigationCompleted", tier1Result.RecommendedDisposition.ToString(), new Dictionary<string, string>
            {
                ["classification"] = tier1Result.Classification.ToString(),
                ["confidence"] = tier1Result.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                ["summary"] = tier1Result.Summary,
                ["reasoningSummary"] = tier1Result.ReasoningSummary,
                ["topHypothesis"] = tier1Result.Hypotheses.Count > 0 ? tier1Result.Hypotheses[0].Description : string.Empty,
            }, cancellationToken).ConfigureAwait(false);

            if (tier1Result.RecommendedDisposition == AgentDisposition.RequestMoreEvidence)
            {
                if (run.Tier1Attempts >= _options.MaxTier1Attempts ||
                    run.EvidenceAttempts >= _options.MaxEvidenceCollectionAttempts)
                {
                    // No attempts remain to gather or reassess evidence; escalate instead of looping.
                    break;
                }

                await TransitionAsync(run, IncidentWorkflowState.AwaitingEvidence, cancellationToken).ConfigureAwait(false);
                if (!await TryCollectEvidenceAsync(run, cancellationToken).ConfigureAwait(false))
                {
                    await FailAsync(run, "Additional evidence collection failed after the maximum number of attempts.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                continue;
            }

            break;
        }

        // Tier 1 produced a remediation plan for the incident the rule-based path
        // could not resolve. By default the plan is shared with Tier 2 for an
        // independent risk assessment instead of being executed directly.
        if (tier1Result.RecommendedDisposition == AgentDisposition.Resolve && tier1Result.ProposedAction is not null)
        {
            await EmitAsync(run, "Tier1RemediationPlanProposed", tier1Result.ProposedAction.ActionType, new Dictionary<string, string>
            {
                ["actionType"] = tier1Result.ProposedAction.ActionType,
                ["target"] = FormatTarget(tier1Result.ProposedAction.Target),
                ["maxExecutionCount"] = tier1Result.ProposedAction.MaxExecutionCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["summary"] = tier1Result.Summary,
                ["reasoningSummary"] = tier1Result.ReasoningSummary,
            }, cancellationToken).ConfigureAwait(false);

            if (_options.Tier1PlansRequireTier2RiskAssessment)
            {
                await EmitAsync(run, "Tier1PlanSharedWithTier2", "risk_assessment_requested", new Dictionary<string, string>
                {
                    ["actionType"] = tier1Result.ProposedAction.ActionType,
                    ["reason"] = "Tier 2 must assess the execution risk of the Tier 1 plan before any command runs.",
                }, cancellationToken).ConfigureAwait(false);

                await TransitionAsync(run, IncidentWorkflowState.Tier2Investigation, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await TransitionAsync(run, IncidentWorkflowState.Executing, cancellationToken).ConfigureAwait(false);
                ExecutionResult? execution = await ExecuteWithRetryAsync(
                    run, tier1Result.ProposedAction, approvalGranted: false,
                    _options.MaxExecutionAttemptsPerAction, cancellationToken).ConfigureAwait(false);

                if (execution is null || execution.Outcome is ExecutionOutcome.Failed or ExecutionOutcome.Rejected)
                {
                    await FailAsync(run,
                        $"Tier 1 remediation action was not executed: {execution?.Message ?? "the execution activity failed"}.",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                await TransitionAsync(run, IncidentWorkflowState.Verifying, cancellationToken).ConfigureAwait(false);
                VerificationResult? verification = await RunActivityAsync(
                    run, "Verification", 1,
                    token => _activities.VerifyTier1RemediationAsync(run.Incident, tier1Result.ProposedAction, run.CorrelationId, token),
                    cancellationToken).ConfigureAwait(false);

                if (verification is not null && verification.Outcome == VerificationOutcome.Passed)
                {
                    await ResolveAsync(run, "Tier 1 remediation succeeded and verification passed.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                // A failed verification after Tier 1 remediation escalates to Tier 2.
                _metrics?.RecordVerificationFailure(verification?.Outcome.ToString() ?? "error");
                await EmitAsync(run, "VerificationCompleted", verification?.Outcome.ToString() ?? "error", null, cancellationToken).ConfigureAwait(false);
                await TransitionAsync(run, IncidentWorkflowState.Tier2Investigation, cancellationToken).ConfigureAwait(false);
            }
        }

        // Tier 2 deep reasoning loop, bounded by the Tier 2 attempt count.
        while (true)
        {
            if (run.State != IncidentWorkflowState.Tier2Investigation)
            {
                await TransitionAsync(run, IncidentWorkflowState.Tier2Investigation, cancellationToken).ConfigureAwait(false);
            }

            if (run.Tier2Attempts >= _options.MaxTier2Attempts)
            {
                await FailAsync(run, "The maximum number of Tier 2 planning attempts was reached without a verified remediation.", cancellationToken).ConfigureAwait(false);
                return;
            }

            run.Tier2Attempts++;
            RemediationPlan? plan = await RunActivityAsync(
                run, "Tier2Planning", run.Tier2Attempts,
                token => _activities.RunTier2PlanningAsync(run.Incident, tier1Result, run.Evidence, run.CorrelationId, token),
                cancellationToken).ConfigureAwait(false);

            if (plan is null)
            {
                if (run.Tier2Attempts >= _options.MaxTier2Attempts)
                {
                    await FailAsync(run, "Tier 2 planning failed after the maximum number of attempts.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                // A failed planning attempt keeps the workflow in Tier2Investigation.
                continue;
            }

            await EmitAsync(run, "Tier2PlanningCompleted", plan.RiskLevel.ToString(), new Dictionary<string, string>
            {
                ["riskLevel"] = plan.RiskLevel.ToString(),
                ["requiresApproval"] = plan.RequiresApproval ? "true" : "false",
                ["actionCount"] = plan.Actions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["summary"] = plan.Summary,
                ["rootCauseHypothesis"] = plan.RootCauseHypothesis.Description,
            }, cancellationToken).ConfigureAwait(false);

            bool approvalRequired = plan.RequiresApproval || _options.Tier2PlansAlwaysRequireApproval;

            // Tier 2 shares its execution-risk assessment so the console can show
            // a human what would run and how risky policy considers it to be.
            await EmitAsync(run, "Tier2RiskAssessmentShared", plan.RiskLevel.ToString(), new Dictionary<string, string>
            {
                ["riskLevel"] = plan.RiskLevel.ToString(),
                ["approvalRequired"] = approvalRequired ? "true" : "false",
                ["commands"] = FormatActions(plan.Actions),
                ["rollbackAvailable"] = plan.Rollback.Count > 0 ? "true" : "false",
                ["verificationStepCount"] = plan.Verification.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["rootCauseHypothesis"] = plan.RootCauseHypothesis.Description,
                ["reasoningSummary"] = plan.ReasoningSummary,
            }, cancellationToken).ConfigureAwait(false);

            bool approvalGranted = false;
            if (approvalRequired)
            {
                // Human approval is an external workflow event with a bounded wait.
                await TransitionAsync(run, IncidentWorkflowState.AwaitingApproval, cancellationToken).ConfigureAwait(false);
                await EmitAsync(run, "ApprovalRequested", plan.RiskLevel.ToString(), new Dictionary<string, string>
                {
                    ["question"] = "Approve execution of the assessed commands?",
                    ["riskLevel"] = plan.RiskLevel.ToString(),
                    ["commands"] = FormatActions(plan.Actions),
                    ["timeoutSeconds"] = _options.EffectiveApprovalTimeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                }, cancellationToken).ConfigureAwait(false);

                ApprovalDecision decision;
                using (Activity? approvalSpan = AgenticOpsActivitySource.StartSpan(SpanNames.ApprovalWait, run.Correlation))
                {
                    decision = await _approvalGate.WaitForApprovalAsync(
                        run.Incident, plan, _options.EffectiveApprovalTimeout, cancellationToken).ConfigureAwait(false);
                    AgenticOpsActivitySource.RecordSuccess(approvalSpan, decision.Outcome.ToString());
                }

                await EmitAsync(run, "ApprovalCompleted", decision.Outcome.ToString(), new Dictionary<string, string>
                {
                    ["approver"] = decision.Approver ?? string.Empty,
                    ["reason"] = decision.Reason ?? string.Empty,
                }, cancellationToken).ConfigureAwait(false);

                switch (decision.Outcome)
                {
                    case ApprovalOutcome.Rejected:
                        await TransitionAsync(run, IncidentWorkflowState.Rejected, cancellationToken).ConfigureAwait(false);
                        run.Summary = "The remediation plan was rejected by a human approver.";
                        return;
                    case ApprovalOutcome.TimedOut:
                        await TransitionAsync(run, IncidentWorkflowState.Terminated, cancellationToken).ConfigureAwait(false);
                        run.Summary = "No approval decision arrived before the timeout; the workflow terminated safely.";
                        return;
                    case ApprovalOutcome.Approved:
                        approvalGranted = true;
                        break;
                }
            }

            // Execute the plan actions in order.
            await TransitionAsync(run, IncidentWorkflowState.Executing, cancellationToken).ConfigureAwait(false);
            bool executionSucceeded = true;
            string executionFailureMessage = string.Empty;
            foreach (RemediationAction action in plan.Actions)
            {
                ExecutionResult? execution = await ExecuteWithRetryAsync(
                    run, action, approvalGranted, _options.MaxExecutionAttemptsPerAction, cancellationToken).ConfigureAwait(false);

                if (execution is null || execution.Outcome is ExecutionOutcome.Failed or ExecutionOutcome.Rejected)
                {
                    executionSucceeded = false;
                    executionFailureMessage = execution?.Message ?? "the execution activity failed";
                    break;
                }
            }

            if (!executionSucceeded)
            {
                await RollBackOrFailAsync(run, plan, approvalGranted,
                    $"Plan execution failed: {executionFailureMessage}", cancellationToken).ConfigureAwait(false);
                return;
            }

            // Verify with bounded attempts. Inconclusive results never count as success.
            await TransitionAsync(run, IncidentWorkflowState.Verifying, cancellationToken).ConfigureAwait(false);
            VerificationResult? verification = null;
            for (int attempt = 1; attempt <= _options.MaxVerificationAttempts; attempt++)
            {
                verification = await RunActivityAsync(
                    run, "Verification", attempt,
                    token => _activities.VerifyPlanAsync(run.Incident, plan, run.CorrelationId, token),
                    cancellationToken).ConfigureAwait(false);

                if (verification is not null && verification.Outcome == VerificationOutcome.Passed)
                {
                    break;
                }
            }

            if (verification is null || verification.Outcome != VerificationOutcome.Passed)
            {
                _metrics?.RecordVerificationFailure(verification?.Outcome.ToString() ?? "error");
            }

            await EmitAsync(run, "VerificationCompleted", verification?.Outcome.ToString() ?? "error", null, cancellationToken).ConfigureAwait(false);

            if (verification is not null && verification.Outcome == VerificationOutcome.Passed)
            {
                await ResolveAsync(run, "Tier 2 remediation succeeded and verification passed.", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (run.Tier2Attempts < _options.MaxTier2Attempts)
            {
                // Verification failed but planning attempts remain: return to Tier 2.
                continue;
            }

            await RollBackOrFailAsync(run, plan, approvalGranted,
                "Verification failed and no Tier 2 planning attempts remain.", cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private async Task<bool> TryCollectEvidenceAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        while (run.EvidenceAttempts < _options.MaxEvidenceCollectionAttempts)
        {
            run.EvidenceAttempts++;
            IReadOnlyList<IncidentEvidence>? evidence = await RunActivityAsync(
                run, "EvidenceCollection", run.EvidenceAttempts,
                token => _activities.CollectEvidenceAsync(run.Incident, run.EvidenceAttempts, run.CorrelationId, token),
                cancellationToken).ConfigureAwait(false);

            if (evidence is not null)
            {
                run.Evidence = evidence;
                return true;
            }
        }

        return false;
    }

    private async Task<ExecutionResult?> ExecuteWithRetryAsync(
        WorkflowRun run,
        RemediationAction action,
        bool approvalGranted,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ExecutionResult? execution = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            execution = await RunActivityAsync(
                run, "Execution", attempt,
                token => _activities.ExecuteActionAsync(run.Incident, action, approvalGranted, run.CorrelationId, token),
                cancellationToken).ConfigureAwait(false);

            if (execution is not null)
            {
                _metrics?.RecordActionExecution(execution.ActionType, execution.Outcome.ToString());
                await EmitAsync(run, "ExecutionCompleted", execution.Outcome.ToString(), new Dictionary<string, string>
                {
                    ["actionType"] = execution.ActionType,
                    ["idempotencyKey"] = execution.IdempotencyKey,
                }, cancellationToken).ConfigureAwait(false);
            }

            // Rejections are deterministic policy decisions; retrying cannot change them.
            if (execution is not null && execution.Outcome is not ExecutionOutcome.Failed)
            {
                return execution;
            }
        }

        return execution;
    }

    private async Task RollBackOrFailAsync(
        WorkflowRun run,
        RemediationPlan plan,
        bool approvalGranted,
        string reason,
        CancellationToken cancellationToken)
    {
        if (plan.Rollback.Count == 0)
        {
            await FailAsync(run, reason + " No rollback actions were defined.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await TransitionAsync(run, IncidentWorkflowState.RollingBack, cancellationToken).ConfigureAwait(false);
        using Activity? rollbackSpan = AgenticOpsActivitySource.StartSpan(SpanNames.Rollback, run.Correlation);
        bool rollbackSucceeded = true;
        foreach (RemediationAction action in plan.Rollback)
        {
            ExecutionResult? execution = await ExecuteWithRetryAsync(
                run, action, approvalGranted, _options.MaxRollbackAttemptsPerAction, cancellationToken).ConfigureAwait(false);
            if (execution is null || execution.Outcome is ExecutionOutcome.Failed or ExecutionOutcome.Rejected)
            {
                rollbackSucceeded = false;
            }
        }

        if (rollbackSucceeded)
        {
            AgenticOpsActivitySource.RecordSuccess(rollbackSpan, "success");
        }
        else
        {
            AgenticOpsActivitySource.RecordFailure(rollbackSpan, "RollbackIncomplete");
        }

        await FailAsync(run,
            reason + (rollbackSucceeded ? " Rollback completed." : " Rollback did not complete cleanly."),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> RunActivityAsync<T>(
        WorkflowRun run,
        string activityName,
        int attemptNumber,
        Func<CancellationToken, Task<T>> activity,
        CancellationToken cancellationToken)
        where T : class
    {
        using Activity? span = AgenticOpsActivitySource.StartSpan(GetSpanName(activityName), run.Correlation, attemptNumber);
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        try
        {
            T result = await activity(cancellationToken).ConfigureAwait(false);
            AgenticOpsActivitySource.RecordSuccess(span, "success");
            RecordTierDuration(activityName, _timeProvider.GetUtcNow() - startedAt);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AgenticOpsActivitySource.RecordFailure(span, exception.GetType().Name);
            RecordTierDuration(activityName, _timeProvider.GetUtcNow() - startedAt);
            await EmitAsync(run, activityName + "Failed", "error", new Dictionary<string, string>
            {
                ["errorCategory"] = exception.GetType().Name,
                ["attemptNumber"] = attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["durationMs"] = (_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            }, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    private static string FormatTarget(ActionTarget target) =>
        $"{target.Namespace}/{target.ResourceType}/{target.ResourceName}";

    private static string FormatActions(IReadOnlyList<RemediationAction> actions) =>
        actions.Count == 0
            ? "none"
            : string.Join(", ", actions.Select(action => $"{action.ActionType} -> {FormatTarget(action.Target)}"));

    private static string GetSpanName(string activityName) => activityName switch    {
        "EvidenceCollection" => SpanNames.EvidenceCollection,
        "RuleEvaluation" => SpanNames.RuleEvaluation,
        "Tier1Investigation" => SpanNames.Tier1Investigation,
        "Tier2Planning" => SpanNames.Tier2Planning,
        "Execution" => SpanNames.Execution,
        "Verification" => SpanNames.Verification,
        _ => "activity." + activityName,
    };

    private void RecordTierDuration(string activityName, TimeSpan duration)
    {
        switch (activityName)
        {
            case "Tier1Investigation":
                _metrics?.RecordTier1Duration(duration);
                break;
            case "Tier2Planning":
                _metrics?.RecordTier2Duration(duration);
                break;
        }
    }

    private async Task ResolveAsync(WorkflowRun run, string summary, CancellationToken cancellationToken)
    {
        await TransitionAsync(run, IncidentWorkflowState.Resolved, cancellationToken).ConfigureAwait(false);
        run.Summary = summary;
    }

    private async Task FailAsync(WorkflowRun run, string summary, CancellationToken cancellationToken)
    {
        await TransitionAsync(run, IncidentWorkflowState.Failed, cancellationToken).ConfigureAwait(false);
        run.Summary = summary;
    }

    private async Task TransitionAsync(WorkflowRun run, IncidentWorkflowState to, CancellationToken cancellationToken)
    {
        WorkflowStateMachine.EnsureValid(run.State, to);
        IncidentWorkflowState from = run.State;
        run.MoveTo(to);
        if (to == IncidentWorkflowState.Tier2Investigation && !run.EscalationRecorded)
        {
            run.EscalationRecorded = true;
            _metrics?.RecordIncidentEscalated();
        }

        await EmitAsync(run, "StateChanged", to.ToString(), new Dictionary<string, string>
        {
            ["from"] = from.ToString(),
            ["to"] = to.ToString(),
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitAsync(
        WorkflowRun run,
        string eventType,
        string? outcome,
        IReadOnlyDictionary<string, string>? details,
        CancellationToken cancellationToken)
    {
        var lifecycleEvent = new IncidentLifecycleEvent(
            SchemaVersions.V1,
            Guid.NewGuid().ToString("N"),
            run.Incident.IncidentId,
            run.CorrelationId,
            eventType,
            ComponentName,
            _timeProvider.GetUtcNow(),
            AttemptNumber: 1,
            Outcome: outcome,
            WorkflowInstanceId: run.WorkflowInstanceId,
            Details: details);

        try
        {
            await _eventPublisher.PublishAsync(lifecycleEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Lifecycle publishing must never block the remediation path. The
            // Dapr-hosted publisher adds its own retry and dead-letter handling.
        }
    }

    private sealed class WorkflowRun
    {
        public WorkflowRun(Incident incident, string workflowInstanceId, string correlationId, DateTimeOffset startedAt)
        {
            Incident = incident;
            WorkflowInstanceId = workflowInstanceId;
            CorrelationId = correlationId;
            StartedAt = startedAt;
            Correlation = new CorrelationContext(incident.IncidentId, correlationId, ComponentName, workflowInstanceId);
            _stateHistory = [IncidentWorkflowState.Received];
        }

        private readonly List<IncidentWorkflowState> _stateHistory;

        public Incident Incident { get; }

        public string WorkflowInstanceId { get; }

        public string CorrelationId { get; }

        public CorrelationContext Correlation { get; }

        public bool EscalationRecorded { get; set; }

        public DateTimeOffset StartedAt { get; }

        public IncidentWorkflowState State { get; private set; } = IncidentWorkflowState.Received;

        public IReadOnlyList<IncidentWorkflowState> StateHistory => _stateHistory;

        public IReadOnlyList<IncidentEvidence> Evidence { get; set; } = [];

        public string Summary { get; set; } = string.Empty;

        public int EvidenceAttempts { get; set; }

        public int Tier1Attempts { get; set; }

        public int Tier2Attempts { get; set; }

        public void MoveTo(IncidentWorkflowState state)
        {
            State = state;
            _stateHistory.Add(state);
        }
    }
}
