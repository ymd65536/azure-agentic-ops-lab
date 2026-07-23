# AGENTS.md

## 1. Project overview

This repository is an experimental implementation of agentic system operations using Azure, Dapr, Kubernetes, and .NET.

The project name is Azure Agentic Ops Lab. The sample system may also be referred to as Project Resolve.

The objective is not to implement unrestricted autonomous remediation. The objective is to evaluate how incident response can be divided among deterministic workflows, rule-based automation, AI agents, human approval, controlled execution, and verification.

The system must demonstrate the following lifecycle:

```text
Detect
→ Classify
→ Investigate
→ Escalate when necessary
→ Plan
→ Approve when necessary
→ Execute
→ Verify
→ Record
```

The project must support local execution on Kubernetes and later deployment to Azure Kubernetes Service.

## 2. Core design principles

Follow these principles in all implementations.

1. Dapr Workflow owns orchestration

AI agents must not freely decide the global workflow or directly invoke arbitrary agents.

Dapr Workflow must control:

* State transitions
* Activity ordering
* Retry boundaries
* Timeouts
* Escalation
* Human approval
* Execution
* Verification
* Rollback
* Workflow termination

2. LLMs perform only non-deterministic reasoning

Use ordinary code for:

* Schema validation
* Rule matching
* Authorization
* State transitions
* Retry decisions
* Idempotency
* Allow-list validation
* Health checks
* Command execution
* Success and failure determination

Use an LLM for:

* Interpreting evidence
* Generating hypotheses
* Summarizing observations
* Selecting likely causes
* Producing a proposed remediation plan
* Producing incident records from structured events

3. Agents must not execute arbitrary commands

No agent may send an arbitrary shell command directly to the operating system, Kubernetes API, Azure CLI, or Azure API.

Agents must produce structured action plans using predefined action types.

ExecutionService must validate each requested action against:

* JSON schema
* Allowed action types
* Allowed target namespaces
* Allowed target resource types
* Risk policy
* Approval requirements
* Idempotency key
* Maximum execution count

Unknown action types must be rejected.

4. Prefer deterministic routing over agent-driven routing

Use explicit routing rules before asking an LLM to choose the next step.

Examples:

* A known incident pattern may be resolved by RuleEvaluator
* A low-confidence Tier 1 result must be escalated to Tier 2
* A high-risk action must require human approval
* A failed verification must start rollback or terminate safely

5. Every operation must be observable and auditable

Every workflow transition, agent request, agent response, tool invocation, action plan, approval, execution result, and verification result must include:

* Incident ID
* Workflow instance ID
* Correlation ID
* Component name
* Timestamp
* Attempt number
* Outcome
* Duration
* Error category when applicable

Do not log secrets, access tokens, raw credentials, or sensitive environment variables.

6. Design for failure and duplicate delivery

Assume that:

* Pods may restart
* Messages may be delivered more than once
* Activities may time out
* LLM calls may fail
* LLM responses may be invalid
* An approval may arrive late
* A dependency may be unavailable
* A workflow may resume after a process restart

All external side effects must be idempotent.

## 3. Initial project scope

Implement only the following components during the first milestone:

* IncidentApi
* IncidentWorkflow
* RuleEvaluator
* Tier1SreAgent
* Tier2SreAgent
* ExecutionService
* VerificationService
* ScribeService
* Shared contracts and observability libraries

Do not add the following unless explicitly requested:

* Web-based management UI
* Slack or Microsoft Teams integration
* PagerDuty API integration
* Production Azure resource modification
* Autonomous execution of high-risk actions
* Vector database
* Multiple workflow engines
* Additional agents
* Dynamic agent creation
* Agent-to-agent free-form chat
* Self-modifying prompts
* Long-term autonomous memory

## 4. Agent roles

### 4.1 Tier 1 SRE Agent

Tier 1 is the fast path.

Responsibilities:

* Review incident metadata
* Review supplied logs, metrics, traces, and deployment metadata
* Match observations with known operational patterns
* Use the Insights capability to search supplied runbooks and prior cases
* Produce a concise investigation result
* Resolve low-complexity incidents when an approved deterministic action exists
* Escalate uncertain, complex, or high-risk incidents to Tier 2

Tier 1 must not:

* Execute actions directly
* Create arbitrary commands
* Modify infrastructure
* Skip required approval
* Continue investigating indefinitely
* Invoke Tier 2 repeatedly

Tier 1 output must conform to a versioned structured schema.

Required output fields:

```json
{
  "schemaVersion": "1.0",
  "incidentId": "string",
  "classification": "known|unknown|ambiguous",
  "summary": "string",
  "observations": [],
  "hypotheses": [],
  "confidence": 0.0,
  "recommendedDisposition": "resolve|escalate|request_more_evidence",
  "proposedAction": null,
  "missingEvidence": [],
  "reasoningSummary": "string"
}
```

Do not expose private chain-of-thought. `reasoningSummary` must contain only a concise explanation based on observable evidence.

### 4.2 Insights capability

Insights is a Tier 1 sub-capability, not an independently orchestrating top-level agent.

Responsibilities:

* Search known incident patterns
* Search runbooks
* Search prior incident summaries
* Return relevant evidence with source identifiers
* Identify possible preventive improvements

Insights must return structured retrieval results. It must not decide whether an action is executed.

For the first milestone, implement Insights using local version-controlled fixtures under `scenarios` or `knowledge`. Do not introduce a vector database unless evaluation proves that keyword and metadata search are insufficient.

### 4.3 Tier 2 SRE Agent

Tier 2 is the deep reasoning path.

Invoke Tier 2 only when one or more of the following conditions apply:

* Tier 1 confidence is below the configured threshold
* Multiple services are affected
* The root cause is ambiguous
* The incident does not match a known pattern
* The proposed action has medium or high risk
* Tier 1 requests escalation
* Verification fails after a Tier 1 remediation

Responsibilities:

* Review the complete structured Tier 1 handoff
* Generate and compare hypotheses
* Analyze impact and dependencies
* Produce a remediation plan
* Assign a risk level
* Define verification criteria
* Define rollback steps when possible
* State whether human approval is required

Tier 2 must produce a structured plan. Free-form commands are prohibited.

Required output shape:

```json
{
  "schemaVersion": "1.0",
  "incidentId": "string",
  "summary": "string",
  "rootCauseHypothesis": {
    "description": "string",
    "confidence": 0.0,
    "evidenceIds": []
  },
  "riskLevel": "low|medium|high",
  "requiresApproval": true,
  "actions": [],
  "verification": [],
  "rollback": [],
  "reasoningSummary": "string"
}
```

### 4.4 Scribe Service

Scribe is an asynchronous consumer and must not be part of the critical remediation path.

Responsibilities:

* Subscribe to incident lifecycle events through Dapr Pub/Sub
* Build an ordered incident timeline
* Generate a final incident summary
* Record actions, approvals, and results
* Produce a post-incident draft from structured events

Scribe failure must not block investigation, remediation, or verification.

Prefer deterministic timeline construction. Use an LLM only to produce a human-readable summary from the completed structured timeline.

## 5. Workflow requirements

Implement the incident workflow using explicit states.

Recommended states:

```text
Received
Classifying
RuleEvaluation
Tier1Investigation
AwaitingEvidence
Tier2Investigation
AwaitingApproval
Executing
Verifying
RollingBack
Resolved
Rejected
Failed
Terminated
```

The workflow must enforce valid transitions.

Examples:

* `Tier1Investigation` may transition to `Resolved`, `Tier2Investigation`, or `AwaitingEvidence`
* `Tier2Investigation` may transition to `AwaitingApproval`, `Executing`, or `Failed`
* `AwaitingApproval` may transition to `Executing`, `Rejected`, or `Terminated`
* `Executing` may transition to `Verifying`, `RollingBack`, or `Failed`
* `Verifying` may transition to `Resolved`, `Tier2Investigation`, `RollingBack`, or `Failed`

Implement maximum attempt counts for:

* Evidence collection
* Tier 1 inference
* Tier 2 inference
* Execution
* Verification
* Rollback

A workflow must terminate safely when a maximum attempt count is reached.

Human approval must be represented as an external workflow event. Do not hold an HTTP request open while waiting for approval.

## 6. Dapr usage

Use Dapr building blocks as follows:

* Workflow for incident orchestration
* Service Invocation for synchronous service calls
* Pub/Sub for lifecycle events, Scribe, and audit consumers
* State Management for application state outside workflow history when required
* Secret Store abstraction for secret references
* Resiliency policies for timeouts and bounded retries

Do not use Dapr merely as a wrapper around direct HTTP calls. Preserve clear boundaries between application logic and Dapr infrastructure.

Dapr component names must remain stable across environments.

Recommended logical component names:

```text
incident-pubsub
incident-state
secret-store
```

Local and Azure environments may use different underlying implementations while keeping the logical names unchanged.

## 7. Kubernetes requirements

The primary local environment is Kubernetes using k3d or kind.

Each Dapr-enabled workload must define:

```yaml
dapr.io/enabled: "true"
dapr.io/app-id: "<stable-app-id>"
dapr.io/app-port: "<application-port>"
```

Use a dedicated namespace such as:

```text
agentic-ops
```

Provide:

* Resource requests and limits
* Readiness probes
* Liveness probes
* Non-root containers
* Read-only root filesystem where practical
* Explicit service accounts
* Minimal RBAC
* Pod disruption-safe behavior where applicable

Do not grant cluster-admin permissions to application services.

ExecutionService must use a dedicated service account with the minimum permissions required for the current scenario.

For the first milestone, ExecutionService should default to mock or dry-run mode.

## 8. Local and Azure environments

### Local environment

Use:

* k3d or kind
* Dapr on Kubernetes
* Redis for development state and Pub/Sub where appropriate
* Local container images
* Kubernetes Secrets only for non-production development values
* Mock incident data
* Mock execution by default

### Azure environment

Design for later use with:

* Azure Kubernetes Service
* Dapr extension for AKS
* Microsoft Foundry model endpoints
* Azure Monitor and Application Insights
* Microsoft Entra Workload ID
* Azure Key Vault
* Azure Service Bus or another supported Pub/Sub component
* An Azure-supported state store

Do not hard-code an Azure-specific SDK into shared business logic when a Dapr abstraction or application interface is appropriate.

Authentication code must support `DefaultAzureCredential` or an equivalent environment-independent credential chain.

Never commit credentials, subscription IDs, tenant IDs, endpoint keys, or generated access tokens.

## 9. Repository organization

Use the following structure:

```text
src/
  BuildingBlocks/
    Contracts/
    Observability/
    AgentRuntime/
    Safety/
  IncidentApi/
  IncidentWorkflow/
  RuleEvaluator/
  Tier1SreAgent/
  Tier2SreAgent/
  ExecutionService/
  VerificationService/
  ScribeService/

tests/
  UnitTests/
  ContractTests/
  WorkflowTests/
  AgentEvaluationTests/
  IntegrationTests/
  ChaosTests/

scenarios/
prompts/
deploy/
infra/
scripts/
docs/
results/
```

Keep domain contracts independent from Dapr, Kubernetes, Azure, and specific LLM SDKs.

Do not place all services in one project.

Do not create a separate repository per service.

## 10. .NET coding requirements

Use the repository-pinned .NET SDK version from `global.json`.

General requirements:

* Enable nullable reference types
* Enable implicit usings
* Treat warnings as errors in CI
* Use async APIs for I/O
* Accept `CancellationToken` in asynchronous operations
* Use dependency injection
* Use `TimeProvider` instead of directly reading the system clock
* Use typed options with startup validation
* Use structured logging
* Use OpenTelemetry for traces and metrics
* Avoid static mutable state
* Avoid service locator patterns
* Avoid blocking calls such as `.Result` and `.Wait()`
* Avoid broad exception catches without classification and logging

Use immutable records for contracts where practical.

Public contracts must be versioned and serializable.

JSON serialization behavior must be explicit and covered by contract tests.

## 11. LLM integration requirements

All model access must be behind an interface.

Example:

```csharp
public interface IAgentModelClient
{
    Task<AgentModelResponse<T>> GenerateStructuredResponseAsync<T>(
        AgentModelRequest request,
        CancellationToken cancellationToken);
}
```

Implement at least:

* A fake model client for deterministic tests
* A configured remote model client
* Response schema validation
* Timeout handling
* Retry classification
* Token usage capture when available
* Latency capture
* Model identifier capture

Prompts must be stored as version-controlled files under `prompts`.

Do not embed large prompts directly in application source code.

Every prompt invocation must record:

* Prompt name
* Prompt version
* Model identifier
* Input token count when available
* Output token count when available
* Total duration
* Validation outcome
* Retry count

Invalid structured output must not be passed downstream.

Use a bounded repair attempt or fail safely.

## 12. Safety requirements

Classify actions into risk levels.

### Low risk

Examples:

* Collect diagnostics
* Query logs
* Query resource status
* Restart a disposable demo workload
* Scale a demo workload within a predefined range

### Medium risk

Examples:

* Restart a stateful component
* Roll back a deployment
* Change a feature flag
* Modify a non-production configuration value

### High risk

Examples:

* Delete resources
* Modify production networking
* Change identity or access policy
* Disable security controls
* Modify or delete persistent data
* Execute an arbitrary command

Rules:

* Low-risk actions may be executed automatically only in explicitly configured demo environments
* Medium-risk actions require approval by default
* High-risk actions must be rejected in the initial implementation
* Unknown actions must be treated as high risk
* The agent cannot downgrade an action's risk classification
* Policy code has final authority over model output

## 13. Observability requirements

Use OpenTelemetry-compatible instrumentation.

Create spans for:

* Incident ingestion
* Workflow execution
* Workflow activities
* Rule evaluation
* Tier 1 inference
* Tier 2 inference
* Service invocation
* Approval wait
* Execution
* Verification
* Rollback
* Scribe processing

Recommended metrics:

```text
incident_total
incident_resolved_total
incident_failed_total
incident_escalated_total
incident_duration_seconds
tier1_duration_seconds
tier2_duration_seconds
agent_model_request_total
agent_model_failure_total
agent_model_input_tokens
agent_model_output_tokens
tool_invocation_total
action_execution_total
action_rejected_total
workflow_resume_total
duplicate_event_total
verification_failure_total
```

Do not use high-cardinality values such as incident IDs as metric labels.

Incident IDs may be included in traces and logs.

## 14. Testing strategy

### Unit tests

Test:

* Rule matching
* Risk classification
* State transitions
* Action validation
* Idempotency
* Retry decisions
* Contract serialization
* Verification logic

### Contract tests

Test all request and response schemas between services.

Golden JSON fixtures may be stored under the relevant scenario.

### Workflow tests

Test:

* Tier 1 resolution
* Tier 2 escalation
* Approval accepted
* Approval rejected
* Approval timeout
* Execution failure
* Verification failure
* Rollback
* Maximum attempt termination
* Workflow restart and continuation

### Agent evaluation tests

Use fixed scenarios and evaluate:

* Correct classification
* Correct escalation decision
* Root cause accuracy
* Unsupported claim count
* Dangerous action proposal count
* Schema compliance
* Latency
* Input tokens
* Output tokens
* Number of tool calls

Agent evaluation tests must not assert exact prose equality. Assert structured fields and measurable criteria.

### Integration tests

Test the system on local Kubernetes with Dapr enabled.

### Chaos tests

Test at least:

* Tier 1 Pod deletion during investigation
* Tier 2 unavailability
* ExecutionService timeout
* Duplicate Pub/Sub event
* Scribe outage
* Redis restart in the local environment
* Workflow process restart
* Delayed approval event

## 15. Initial scenarios

Implement scenarios in this order.

### Scenario 001: Known routing configuration error

Purpose:

* Demonstrate Tier 1 fast-path resolution
* Avoid Tier 2 invocation
* Use an approved low-risk mock action

Expected result:

* Known pattern matched
* Tier 1 produces high confidence
* Execution plan passes policy
* Verification succeeds
* Scribe records the incident

### Scenario 002: Ambiguous increase in HTTP 404 responses

Purpose:

* Demonstrate structured escalation
* Compare multiple hypotheses
* Invoke Tier 2
* Require human approval

Expected result:

* Tier 1 identifies ambiguity
* Tier 2 proposes a remediation plan
* Execution waits for approval
* Mock execution succeeds
* Verification resolves the incident

### Scenario 003: Dependency timeout

Purpose:

* Demonstrate that restarting the local service may not solve an external dependency failure
* Prevent repeated ineffective actions
* Verify bounded retries and safe termination

Expected result:

* The system avoids an infinite restart loop
* The incident remains unresolved or is escalated
* The final record explains the remaining uncertainty

## 16. Implementation order

Unless explicitly instructed otherwise, work in this order:

1. Create shared contracts
2. Create scenario fixtures
3. Implement RuleEvaluator
4. Implement fake model client
5. Implement Tier 1 structured output
6. Implement Tier 2 structured output
7. Implement action policy and mock ExecutionService
8. Implement VerificationService
9. Implement Dapr Workflow
10. Implement approval external event
11. Implement lifecycle Pub/Sub events
12. Implement ScribeService
13. Add OpenTelemetry instrumentation
14. Add Kubernetes manifests
15. Add local bootstrap scripts
16. Add integration and chaos tests
17. Add optional remote model integration
18. Add optional Azure deployment resources

Do not start with Azure infrastructure provisioning.

Do not start with production remediation.

## 17. Change discipline

Before making a change:

* Read the relevant contracts
* Read the relevant scenario
* Identify affected services
* Identify affected tests
* Preserve existing architectural boundaries

After making a change:

* Format the code
* Build the complete solution
* Run unit tests
* Run contract tests
* Run affected workflow tests
* Update documentation when behavior changes
* Report tests that were not run

Do not silently alter public schemas.

Do not add a new dependency without explaining why the existing platform or standard library is insufficient.

Do not introduce a new agent when the responsibility can be implemented as:

* A deterministic function
* A workflow activity
* A tool
* A policy
* A Pub/Sub consumer
* An existing agent capability

## 18. Definition of done

A feature is complete only when:

* The implementation follows the workflow and safety boundaries
* Public contracts are documented
* Tests cover normal and failure paths
* Logs and traces include correlation data
* No secrets are committed
* Invalid model output is handled safely
* Duplicate execution is prevented
* Documentation is updated
* Local Kubernetes deployment succeeds
* The relevant scenario can be reproduced from a script

## 19. Commands

Prefer repository scripts over undocumented manual commands.

Expected scripts:

```text
scripts/bootstrap-local.sh
scripts/build-images.sh
scripts/deploy-local.sh
scripts/run-scenario.sh
scripts/inject-failure.sh
scripts/collect-results.sh
```

The README must document the exact commands required to:

* Create the local cluster
* Install Dapr
* Build images
* Deploy the system
* Run each scenario
* Submit an approval event
* Inspect workflow state
* Collect logs, traces, and evaluation results
* Remove the local environment

## 20. Final architectural constraint

This project evaluates controlled autonomy.

The implementation must preserve the following authority order:

```text
Policy and workflow
    override
Agent recommendations
    which override
No action
```

When evidence is insufficient, output is invalid, policy is unclear, or execution risk is unknown, the system must stop, escalate, or request human intervention rather than guessing.
