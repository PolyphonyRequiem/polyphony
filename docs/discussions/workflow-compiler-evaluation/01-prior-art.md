---
doc_type: discussion
status: exploratory
synopsis: Industry survey of typed-orchestration systems that emit runtime config (AWS CDK, cdk8s, CDKTF, Flyte). Companion to the compiler-lite evaluation.
---

# Prior art / industry survey: typed orchestration that emits runtime config

## Executive summary

- The strongest industry pattern is **host-language code as the authoring surface, separate runtime artifact as the execution surface**. AWS CDK synthesizes CloudFormation/ASL, cdk8s synthesizes Kubernetes YAML, CDKTF synthesized Terraform JSON, and Flyte compiles Python into a registered workflow artifact rather than running the authoring function at runtime (https://docs.aws.amazon.com/cdk/v2/guide/constructs.html, https://docs.aws.amazon.com/cdk/api/v2/docs/aws-cdk-lib.aws_stepfunctions-readme.html, https://cdk8s.io/docs/latest/, https://developer.hashicorp.com/terraform/cdktf/concepts/cdktf-architecture, https://docs.flyte.org/en/latest/flyte_fundamentals/tasks_workflows_and_launch_plans.html).
- The most successful systems make **dataflow typed and explicit**. Pulumi `Input<T>/Output<T>`, Dagger typed artifacts, and Flyte task signatures let the authoring language carry dependency and artifact information; systems that rely on side channels such as Airflow XCom or stringly YAML parameters are where users report the most pain (https://www.pulumi.com/docs/iac/concepts/inputs-outputs/, https://docs.dagger.io/, https://docs.flyte.org/en/latest/flyte_fundamentals/tasks_workflows_and_launch_plans.html, https://airflow.apache.org/docs/apache-airflow/stable/tutorial/taskflow.html).
- The big failure mode is shipping a **thin wrapper over someone else’s config format**. CDKTF officially reached deprecation in December 2025; it synthesized Terraform JSON but added no new runtime capability, so the wrapper had to chase the underlying ecosystem forever (https://developer.hashicorp.com/terraform/cdktf/concepts/cdktf-architecture).
- Durable-execution systems such as Temporal, DBOS, Durable Functions, and Restate prove that “the orchestrator is code” can work, but only if the runtime owns replay, history, and determinism. That is a fundamentally different bet from “generate YAML/JSON from C# and let another runtime execute it” (https://docs.temporal.io/workflows, https://docs.temporal.io/develop/dotnet/workflows/basics, https://docs.dbos.dev/typescript/tutorials/workflow-tutorial, https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-code-constraints, https://docs.restate.dev/concepts/durable_execution).
- For Polyphony specifically, the closest analog is **AWS CDK / Step Functions structurally**, **Flyte operationally**, and **Dagger ergonomically**: typed host-language composition, explicit synthesis/registration, and artifact-first outputs.

## Pulumi and CDKTF

### Pulumi

**Problem solved.** Pulumi is infrastructure-as-code for cloud resources across many providers. The target runtime is not the language process itself; it is the Pulumi engine plus provider plugins, which reconcile desired state against the actual cloud APIs (https://www.pulumi.com/docs/iac/guides/basics/how-pulumi-works/).

**Authoring surface.** Authors write ordinary programs in TypeScript, Python, Go, Java, or C#. The .NET example is literally `Deployment.RunAsync(() => new Aws.S3.Bucket(...))`, i.e. a normal language host that constructs resources and exports outputs (https://www.pulumi.com/docs/iac/guides/basics/how-pulumi-works/).

**Runtime.** On `pulumi up`, the CLI launches the language host, the program registers resources with the engine, and the engine talks to provider plugins. The program is a **synthesis/discovery phase**; constructing a resource object does not itself create the cloud resource. That split is the key transferable idea for Polyphony: authoring code can stay rich and typed while execution remains delegated to an external orchestrator/runtime (https://www.pulumi.com/docs/iac/guides/basics/how-pulumi-works/).

**Type-system unification story.** Pulumi’s core abstraction is `Input<T>/Output<T>`. Outputs represent values only known after provisioning; threading an `Output<T>` into another resource’s input automatically records a dependency edge. That means the host language is not just a nicer syntax; it is the place where dependency semantics live (https://www.pulumi.com/docs/iac/concepts/inputs-outputs/).

**What worked.** Pulumi succeeded where many “IaC in real languages” attempts stalled because it added a real execution model, not just sugar over YAML. The engine/provider split, state model, previews, and first-class dependency typing made the language surface operationally meaningful. Pulumi also explicitly added Pulumi YAML later for simpler or generated cases, positioning YAML as an easier surface for small/generated stacks rather than the primary power-user abstraction (https://www.pulumi.com/blog/pulumi-yaml/).

**Pain points / anti-patterns.** The hard lesson is that synth-time values and deploy-time values must be distinct. Users routinely want to branch or print on an `Output<T>` as if it were a concrete value; Pulumi’s answer is `apply`, and the docs explicitly warn against creating resources inside `apply` because preview can stop matching actual execution (https://www.pulumi.com/docs/iac/concepts/inputs-outputs/apply/). Transferable lesson: if Polyphony moves to C#, it needs a first-class distinction between compile-time-known values and runtime-produced values.

### CDKTF

**Problem solved.** CDKTF tried to give Terraform users a CDK-style authoring experience in a real language while still targeting Terraform as the runtime. It synthesized JSON configuration files that Terraform then planned/applied (https://developer.hashicorp.com/terraform/cdktf/concepts/cdktf-architecture).

**Authoring surface.** `App -> Stack -> Resource`, polyglot via jsii-generated bindings. This is the direct “typed language emits config” pattern (https://developer.hashicorp.com/terraform/cdktf/concepts/cdktf-architecture).

**Runtime.** Pure compile/synth layer. `cdktf synth` emits Terraform JSON; Terraform is still the executor. The authoring language contributes no runtime behavior after synthesis (https://developer.hashicorp.com/terraform/cdktf/concepts/cdktf-architecture).

**Type-system unification story.** Provider schemas were pulled into generated bindings, but the runtime truth still belonged to Terraform’s schema/config world, not a single end-to-end type system.

**Transferable lesson.** This is the cleanest evidence for what **not** to do shallowly. HashiCorp now marks CDKTF deprecated as of 2025-12-10. The warning for Polyphony is sharp: if C# only wraps existing workflow YAML without adding stronger contracts, compile-time validation, artifact identity, or better operational semantics, the wrapper becomes permanent catch-up tax (https://developer.hashicorp.com/terraform/cdktf/concepts/cdktf-architecture).

## AWS CDK and cdk8s

**Problem solved.** AWS CDK turns typed code into CloudFormation; cdk8s turns typed code into pure Kubernetes YAML (https://docs.aws.amazon.com/cdk/v2/guide/constructs.html, https://cdk8s.io/docs/latest/).

**Authoring surface.** The core shape is a construct tree. The CDK docs define constructs as the building blocks of the application and explicitly categorize them as L1/L2/L3: raw resource mapping, curated resource wrapper, and opinionated pattern (https://docs.aws.amazon.com/cdk/v2/guide/constructs.html). cdk8s uses the same model: `App`, `Chart`, `Construct`, then synthesize charts to `dist/*.k8s.yaml` (https://cdk8s.io/docs/latest/).

**Runtime.** CDK itself is not the runtime. `cdk synth` produces CloudFormation templates, and CloudFormation executes them. cdk8s similarly only defines applications; a later `kubectl apply` or GitOps tool consumes the emitted YAML (https://cdk8s.io/docs/latest/).

**Type-system unification story.** CDK gives you typed constructs, but it also has to represent deploy-time values and target-language distinctions. The Step Functions construct library is especially relevant: you define workflows as objects and chainable states, but the end product is an ASL state machine document. CDK even exposes typed factory methods like `Pass.jsonata()` and `Pass.jsonPath()` so the data-routing language is explicit in the type surface instead of being a stringly runtime mistake (https://docs.aws.amazon.com/cdk/api/v2/docs/aws-cdk-lib.aws_stepfunctions-readme.html).

**What transfers.** This is probably the closest mechanical analog to the Polyphony proposal. If Polyphony authors a workflow graph in C#, the CDK lesson is: keep a raw layer, a sane-default typed layer, and an opinionated pattern layer. Also, preserve an escape hatch down to the raw emitted shape; over-opinionated L3 constructs become cages.

**Hard-won lesson.** CDK users constantly hit “token leakage”: values known only at deploy time accidentally escape into compile-time strings, filenames, or branching logic. Polyphony will need the same boundary discipline for verb outputs, human-gate decisions, generated branch names, PR URLs, and similar runtime values.

## Airflow TaskFlow, Dagster, Prefect, and Flyte

### Airflow TaskFlow API

**Problem solved.** Airflow schedules and retries batch/data workflows. TaskFlow was introduced to make DAG authoring more Pythonic: decorate normal functions, call them like functions, and let Airflow infer dependencies and data passing (https://airflow.apache.org/docs/apache-airflow/stable/tutorial/taskflow.html).

**Authoring surface.** `@dag` and `@task` decorated functions. The author writes `load(transform(extract()))`, which is much nicer than hand-wiring operator objects (https://airflow.apache.org/docs/apache-airflow/stable/tutorial/taskflow.html).

**Runtime.** Still classic Airflow: a scheduler parses DAG files, creates task instances, and uses XCom under the hood for data passage. TaskFlow improved authoring, not the runtime contract (https://airflow.apache.org/docs/apache-airflow/stable/tutorial/taskflow.html).

**Type-system unification story.** Weak. Python type hints help humans and IDEs, but the actual runtime data channel is still XCom. `multiple_outputs=True` is effectively a convention for splitting returned dicts into XCom keys, not a durable typed contract (https://airflow.apache.org/docs/apache-airflow/stable/tutorial/taskflow.html).

**Lesson for Polyphony.** Host-language ergonomics alone are not enough. If the actual cross-step boundary is still stringly or side-channel based, the system stays brittle.

### Dagster

**Problem solved.** Dagster reframed orchestration around software-defined assets. Instead of thinking primarily in terms of steps, authors define assets, upstream asset keys, and the code that materializes them (https://docs.dagster.io/concepts/assets/software-defined-assets).

**Authoring surface.** Python decorators: `@asset`, `@multi_asset`, `@graph_asset`. This is an important move away from raw DAG authoring and toward typed, named, durable outputs (https://docs.dagster.io/concepts/assets/software-defined-assets).

**Runtime.** Dagster still runs a separate orchestration runtime, but it records asset materializations and metadata as first-class entities.

**Type-system unification story.** Better than Airflow but still not fully compile-time strict. The asset key and asset graph are the main unifying abstractions; the value proposition is observability, lineage, and incremental reasoning around outputs.

**Lesson for Polyphony.** This lines up strongly with Polyphony’s action journal and typed effects work: artifacts and effects should be first-class, not just incidental side effects of step execution.

### Prefect

**Problem solved.** Prefect wants workflow authoring to feel local-first and Python-native. You run a flow by calling a function; workers and work pools decide where it executes later (https://docs.prefect.io/v3/develop/write-flows, https://docs.prefect.io/v3/deploy/infrastructure-concepts/workers).

**Authoring surface.** `@flow` and `@task`, plain Python, child flows, configurable task runners (https://docs.prefect.io/v3/develop/write-flows).

**Runtime.** A worker polls a work pool and provisions the right execution environment: process, Docker, Kubernetes, ECS, ACI, etc. The same authored flow can move between infrastructures without rewriting the flow body (https://docs.prefect.io/v3/deploy/infrastructure-concepts/workers).

**Type-system unification story.** Better boundary validation than Airflow: Prefect validates flow parameters with Pydantic by default and can derive a default version string as a hash of the defining file (https://docs.prefect.io/v3/develop/write-flows). That is directly relevant to Polyphony’s typed CLI boundaries and generated-artifact identity.

**Hard lesson.** Prefect’s own docs note that timeouts for sync tasks on the default thread-pool runner cannot interrupt blocking operations reliably. This is a good reminder that friendly authoring can hide tricky runtime semantics (https://docs.prefect.io/v3/develop/write-flows).

### Flyte

**Problem solved.** Flyte targets large-scale typed data/ML workflows on Kubernetes with strong task signatures, caching, and registration/compilation as explicit steps (https://docs.flyte.org/en/latest/flyte_fundamentals/tasks_workflows_and_launch_plans.html).

**Authoring surface.** Tasks and workflows are defined as typed Python functions. Flyte 2 goes even further: workflows are just tasks that call other tasks, keeping the surface even closer to regular code (https://docs.flyte.org/en/latest/flyte_fundamentals/tasks_workflows_and_launch_plans.html).

**Runtime.** The authored code is compiled/registered into a workflow artifact; the runtime schedules containerized tasks. This is the cleanest “workflow compiler” precedent in the survey.

**Type-system unification story.** Strong. Type annotations drive workflow validation, task interfaces, caching, and resource declarations. Caching, resources, and image selection are declared at the task boundary, not hidden in a separate YAML (https://docs.flyte.org/en/latest/flyte_fundamentals/tasks_workflows_and_launch_plans.html).

**Lesson for Polyphony.** If the team wants “declare in C# and generate workflow just in time,” Flyte is the strongest analog: make compile/register a first-class lifecycle step; make task boundaries typed; and make artifact references, not large payloads, the thing that flows between steps.

## Temporal and DBOS

### Temporal

**Problem solved.** Temporal provides durable execution for long-running, failure-tolerant workflows. The workflow can run for years; replay rebuilds state from event history (https://docs.temporal.io/workflows).

**Authoring surface.** The workflow is ordinary code in a supported SDK, including .NET. Activities are typed method calls from workflow code (https://docs.temporal.io/develop/dotnet/workflows/basics).

**Runtime.** Crucially different from CDK/Flyte. There is no separate config document to emit. The workflow code itself is the runtime definition; Temporal re-executes it against recorded event history and checks that it emits the same commands in the same sequence (https://docs.temporal.io/workflows, https://docs.temporal.io/workflow-definition#deterministic-constraints).

**Type-system unification story.** Strong inside the host language, but bounded by determinism rules. Activities, signals, and updates are typed, yet replay forces a constrained subset of host-language behavior (https://docs.temporal.io/develop/dotnet/workflows/basics).

**Lesson for Polyphony.** This is the right comparison point for “why not just make the orchestrator be C#?” The answer is: you can, but then you inherit replay safety, versioning, determinism, and deployment compatibility as core product concerns. If Polyphony instead emits a workflow artifact for another runtime, it can get most authoring benefits without that class of complexity.

### DBOS

**Problem solved.** DBOS offers durable execution backed by a database. Workflows resume after crashes from the last completed step, and workflow IDs act as idempotency keys (https://docs.dbos.dev/typescript/tutorials/workflow-tutorial).

**Authoring surface.** TypeScript functions or decorators, `DBOS.workflow()` and `DBOS.step()` (https://docs.dbos.dev/typescript/tutorials/workflow-tutorial).

**Runtime.** DB-backed replay/journal. Again, this is host-language runtime execution, not config synthesis.

**Type-system unification story.** Inputs and outputs must be JSON-serializable, but the bigger story is operational simplicity: journaled steps and workflow IDs are first-class.

**Lesson for Polyphony.** DBOS reinforces two transferable ideas even if Polyphony never adopts durable execution: explicit idempotency keys and journal-backed re-entry semantics are extremely valuable; Polyphony’s journal/effects direction is on the right track.

## Argo Workflows, Hera, and Tekton

**Problem solved.** Argo and Tekton are Kubernetes-native workflow/pipeline runtimes. The runtime artifact is Kubernetes CRDs/YAML; controllers reconcile them into pods and task runs (https://argoproj.github.io/argo-workflows/, https://tekton.dev/docs/concepts/overview/).

**Authoring surface.** Mostly YAML. Tekton explicitly frames itself as Kubernetes custom resources and points users at Pipelines-as-Code in a `.tekton/` directory for co-located pipeline definitions (https://tekton.dev/docs/concepts/overview/).

**Typed DSL story.** Hera is the important precedent: a Python-first SDK that makes Argo workflows “simple and intuitive,” lets you decorate Python functions with `@script`, and build DAGs with operators like `A >> [B, C] >> D` (https://hera-workflows.readthedocs.io/en/stable/).

**Runtime.** Still Argo/Tekton controllers. The Python layer is authoring sugar, not the executor.

**What transfers.** Hera proves there is real appetite for typed/programmable authoring on top of a YAML-native workflow engine. Tekton’s `.tekton/` co-location is also relevant: generated artifacts and authored code should live close to the repo they orchestrate, not in a far-away control plane.

**Anti-pattern.** The community has repeatedly learned that “typed wrapper” can turn into “leaky wrapper.” If the author still has to think in Argo template quirks, Kubernetes CRD fields, or script serialization details, the DSL never truly replaces the underlying schema. Polyphony should not merely rename YAML nodes in C#; it should encode higher-order concepts like typed step outputs, policies, fan-out/fan-in, and human gates.

## GitHub Actions reusable workflows and CircleCI orbs

**Problem solved.** Both systems attack reuse inside YAML-first CI/CD. GitHub reusable workflows use `workflow_call`; CircleCI orbs package jobs/commands/executors behind a versioned slug (https://docs.github.com/en/actions/sharing-automations/reusing-workflows, https://circleci.com/docs/orb-intro/).

**Authoring surface.** Still YAML. GitHub reusable workflows support only a few scalar input types (`string`, `boolean`, `number`) plus secrets; CircleCI orb elements are still YAML-level commands/jobs/executors imported by version (https://docs.github.com/en/actions/sharing-automations/reusing-workflows, https://circleci.com/docs/orb-intro/).

**Runtime.** Same old runtime: runners execute shell steps. Reuse is composition, not a new type system.

**Lesson for Polyphony.** This is the best evidence that YAML-only composition eventually tops out. Reusable workflows and orbs reduce duplication, but they do not give the author a real host-language abstraction system, strong artifact typing, or compile-time graph validation.

**Hard-won lesson.** GitHub’s docs explicitly note that environment secrets cannot be passed through `workflow_call`. That is exactly the sort of “looks typed, fails in production shape” problem that stronger generated contracts are meant to eliminate (https://docs.github.com/en/actions/sharing-automations/reusing-workflows).

## Dagger

**Problem solved.** Dagger is the most directly relevant prior art for “CI pipelines as code with a typed SDK.” It explicitly positions itself as replacing YAML-based CI/CD with code (https://docs.dagger.io/features/programmability, https://docs.dagger.io/).

**Authoring surface.** Native SDKs, including .NET. Functions take typed inputs and return typed artifacts such as `Container`, `Directory`, `File`, and `Secret` (https://docs.dagger.io/, https://docs.dagger.io/features/programmability).

**Runtime.** Dagger runs against its own engine. Operations are lazy, just-in-time, and keyed by input content. Intermediate artifacts are built only when needed, and every operation is incremental by default (https://docs.dagger.io/, https://docs.dagger.io/features/programmability).

**Type-system unification story.** Excellent. The GraphQL schema is the canonical contract; SDKs are generated from it; typed artifacts can cross module and language boundaries without serialization gymnastics (https://docs.dagger.io/).

**Lesson for Polyphony.** Dagger’s biggest contribution is not “use code instead of YAML”; it is **artifact-first orchestration**. Steps return typed handles to things that can be further composed. That is a much stronger model than today’s world of stringly `output_map`, ad hoc JSON envelopes, or filesystem conventions.

**Anti-pattern to avoid.** Do not let the underlying transport/schema leak too far upward. Dagger users eventually hit GraphQL IDs and engine internals. Polyphony should keep C# constructs and generated workflow artifacts comprehensible without forcing users to become runtime-plumbing experts.

## Additional relevant prior art

- **Azure Durable Functions / Durable Task**: same replay/determinism family as Temporal, but especially relevant because it is C#-native and has explicit “use deterministic APIs only” guidance. Strong evidence that “orchestrator as C# runtime” is viable but operationally heavier than “C# compiler emits workflow” (https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-code-constraints).
- **Restate**: durable execution with a built-in journal and a server/proxy model. Important because it treats the journal as the core runtime primitive and offers exactly-once durable steps without users building queues and retry frameworks themselves (https://docs.restate.dev/concepts/durable_execution).

## Cross-cutting patterns: where the industry has converged

1. **Separate authoring from execution.** The durable-execution family is the exception; most successful systems compile/synthesize/register an artifact, then hand execution to another runtime.
2. **Make runtime-unknown values explicit in the type system.** Pulumi outputs, CDK tokens, and Temporal replay-safe APIs all exist because “looks like a normal value” is not good enough.
3. **Invest in reusable construct layers.** CDK’s L1/L2/L3 split is the clearest statement of what a sustainable authoring stack looks like.
4. **Treat artifacts and effects as first-class.** Dagster assets, Dagger artifacts, and Flyte typed task outputs all beat loose shell conventions.
5. **Make compile/registration observable and cacheable.** The good systems have an explicit synthesis or registration step with an identity: CDK synth, Flyte register, Pulumi preview/up, Dagger content-addressed execution.

## Anti-patterns: what failed or hurt

- **Thin wrappers over an external schema** without new operational value (CDKTF).
- **Side-channel data passing** instead of typed returns (Airflow XCom as the canonical warning).
- **Hidden compile-time side effects** in author code, which make preview/synth misleading (Pulumi `apply`, Airflow heavy module-scope work).
- **Runtime code injection/serialization tricks** that blur authoring and execution too much (common in Python-on-YAML wrappers).
- **Over-opinionated high-level constructs** with no escape hatch; users always need a way down to the raw runtime shape.

## Specifically for Polyphony

### Closest analog

The closest **structural** analog is **AWS CDK for Step Functions**: author a state machine in a typed host language, synthesize a runtime document, then let an external engine execute it (https://docs.aws.amazon.com/cdk/api/v2/docs/aws-cdk-lib.aws_stepfunctions-readme.html). The closest **operational** analog is **Flyte**: make compile/register an explicit lifecycle step, keep task boundaries typed, and let the runtime execute a compiled artifact (https://docs.flyte.org/en/latest/flyte_fundamentals/tasks_workflows_and_launch_plans.html). The closest **ergonomic** analog is **Dagger**: treat outputs as typed artifacts/handles rather than opaque JSON or environment conventions (https://docs.dagger.io/).

### Design choices Polyphony should steal

1. **Introduce an explicit compile phase.** Not “maybe generate YAML during execution,” but a named step that emits a validated workflow artifact plus schema metadata and a source hash. Flyte and CDK both make this phase first-class.
2. **Adopt an L1/L2/L3 model.** L1 = raw conductor node/edge/script/agent primitive, L2 = typed step/verb wrapper with known contracts and defaults, L3 = reusable work shape such as “implement MG,” “open/merge PR,” or “actionable evidence loop.”
3. **Model runtime values distinctly from compile-time values.** A `WorkflowValue<T>` or equivalent is better than pretending every step output is just a `T`. Pulumi/CDK both prove this is necessary.
4. **Promote artifacts/effects over string envelopes.** Typed effect records and resource handles should be the composition primitive. That aligns with the journal/effects work already underway and avoids the Airflow XCom trap.
5. **Keep escape hatches narrow and explicit.** The CDK lesson is clear: high-level constructs are great only if the author can still drop to the raw emitted shape when needed.

## Open questions for the synthesis step

1. Is the desired end state **single-target synthesis** (Conductor/YAML only) or a future multi-target compiler surface?
2. What is Polyphony’s equivalent of `Output<T>` / token values for data only known after earlier steps run?
3. Should the compiler emit only workflow YAML, or also emit contract schemas, source maps, and effect metadata for linting/observability?
4. Which current PowerShell/helper patterns deserve L2 wrappers versus being preserved as explicit escape hatches?
5. Is the right “unit of reuse” a typed step, a typed sub-workflow/work-shape, or a typed artifact/effect pipeline?
