# GitHub Actions — CI/CD for the Helm Deployment

## 1. Requirements

- Study every file in `D:\git\microservice_0\.github\workflows` as the reference pattern.
- Write GitHub Actions workflows at `D:\git\microservice_1\.github\workflows`, based on `D:\git\microservice_1\helm` (not `kubernetes/` — `helm/` is this repo's CI/CD-driving deployment path).
- Planning stage only as of this doc's last update — no workflow files have been created yet. This doc records the design decided before any of it is written.

## 2. Decisions

| Decision | Choice | Why |
|---|---|---|
| Reference project's 4 workflows (`deploy.yml`, `destroy.yml`, `rollback.yml`, `apply-alloy-secret.yml`) | **Port `deploy.yml`/`destroy.yml`/`rollback.yml`; omit `apply-alloy-secret.yml` entirely** | `microservice_0` runs a real Grafana Cloud/Alloy observability stack; `microservice_1` never wired one up (`KEDA.md` §7 — OTel instrumentation exists in code, exporters are console/no-op only). Nothing to configure a secret for. |
| `deploy.yml`'s "Install Alloy" step | **Omitted** | Same reasoning — no Alloy Helm release exists in this project's `helm/justfile` to install. |
| `deploy.yml`'s Trivy image-vulnerability scan (gates deploy on CRITICAL/HIGH) | **Omitted for this round** | Deliberately descoped, not an oversight — kept the first version of this pipeline smaller. Worth reconsidering later; `microservice_0`'s exact gating pattern (`ignore-unfixed: true`, SARIF upload regardless of pass/fail) is the reference if/when it's added. |
| `deploy.yml`'s `code-scan` job (CodeQL SAST, csharp + javascript-typescript, non-gating) | **Omitted for this round** | Same as Trivy — descoped for the first version, not because CodeQL doesn't apply (it would, csharp-only, no frontend exists here). |
| `rollback.yml` | **Included — and its prerequisite built first** | `microservice_0`'s `rollback.yml` calls `just rollback <env> <revision>`, a `Helm/justfile` recipe + `Helm/scripts/rollback.sh` this project's `helm/` doesn't have yet. Rather than write a workflow that calls something that doesn't exist, `helm/justfile`'s `rollback` recipe and `helm/scripts/rollback.sh` (thin `helm rollback microservice1 -n <namespace> [revision]` wrapper, mirroring the reference exactly) get built as part of this same work. |
| AWS OIDC authentication (`secrets.AWS_GITHUB_ACTIONS_ROLE_ARN`) | **Already satisfied — no new Terraform/GitHub config needed** | `microservice_1_terraform/github-actions-oidc/` already provisions this role (confirmed applied to AWS, confirmed the resulting ARN is already set as this GitHub repo's `AWS_GITHUB_ACTIONS_ROLE_ARN` secret). Workflows can authenticate as soon as they exist — nothing blocking on the AWS/GitHub-config side. |
| `test` job scope | **`dotnet test` on all 3 `.sln`s (`TransactionGateway`, `TransactionService`, `TransactionWorker`) + a separate explicit step for `components/ShardRouting.Tests/ShardRouting.Tests.csproj`** | Mirrors `microservice_0`'s per-service `dotnet test` steps. `ShardRouting.Tests` isn't referenced by any `.sln` (only the non-test `ShardRouting` library is, as a project dependency of `TransactionService.Application`/`TransactionWorker.Domain`), so it needs its own step or its tests would never run in CI at all. No Angular/frontend step — no frontend project exists in this repo. |
| `check-cluster` job | **Ported as-is**, cluster name `microservice1-<environment>` | Same "skip deploy cleanly, don't fail, when the cost-consciously-torn-down cluster doesn't exist right now" pattern as `microservice_0` — matches this project's already-confirmed live cluster naming convention. |
| `deploy` job steps | **`update-kubeconfig` → `install-alb-controller` → `install-external-dns` → `install-cluster-autoscaler` → `build-push` → `deploy-app`, each `working-directory: helm`, each a `just <recipe>` call** | Same one-step-per-`just`-recipe structure as `microservice_0`, for the same per-stage debuggability. No install-alloy step (see above). Works transparently even though several of these `helm/justfile` recipes delegate to `kubernetes/scripts/*.sh` under the hood (`prompting/Helm.md` §2) — `actions/checkout@v4` checks out the whole repo, so both directories are present. |
| Naming/secrets adapted from `microservice0` → `microservice1` | SSM paths (`/microservice1/<env>/...`), concurrency group (`microservice1-deploy-<env>`), cluster name (`microservice1-<env>`), AWS region (`ca-central-1`, unchanged) | Direct 1:1 substitution — same conventions this project has used everywhere else (`Kubernetes.md`, `Terraform.md`, `Helm.md`). |
| Trigger branches | **Unchanged**: push to `master` auto-deploys `develop`; `staging`/`production` only via manual `workflow_dispatch` | This repo uses the same `master`/`develop` branch convention as `microservice_0` (confirmed: `develop` is the working branch, `master` is the PR target). |

## 3. Workflow Layout (proposed, not yet created)

```
.github/workflows/
├── deploy.yml     # push:[master] + workflow_dispatch(environment, tag)
│                  #   test (dotnet test x4) -> check-cluster -> deploy
│                  #   deploy: update-kubeconfig, install-{alb-controller,external-dns,cluster-autoscaler},
│                  #           build-push, deploy-app (all `just <recipe>`, working-directory: helm)
├── destroy.yml    # workflow_dispatch(environment, confirm) - manual only, type-to-confirm
│                  #   update-kubeconfig, destroy-app
└── rollback.yml   # workflow_dispatch(environment, revision, confirm) - manual only, type-to-confirm
                   #   update-kubeconfig, rollback
```

- No `apply-alloy-secret.yml` — see §2.
- `rollback.yml` depends on new, not-yet-written `helm/justfile` (`rollback` recipe) + `helm/scripts/rollback.sh` — see §2's rollback decision. These get built alongside the workflow, not deferred.
- All three workflows share `concurrency: group: microservice1-deploy-${{ inputs.environment || 'develop' }}` (or the no-default-fallback form for `destroy.yml`/`rollback.yml`, which always require an explicit input) — same collision-prevention reasoning as `microservice_0` (a deploy and a destroy/rollback of the same environment must never race).

## 4. Implementation Plan

**Phase 1 — `helm/` rollback capability** ⬜
- ⬜ `helm/scripts/rollback.sh <env> [revision]` — `helm rollback microservice1 <revision> -n microservice1-<env>` (blank revision = Helm's own default, immediately-preceding-revision behavior), after `update-kubeconfig`.
- ⬜ `helm/justfile`'s `rollback` recipe, matching `microservice_0`'s signature (`rollback env="develop" revision="": (update-kubeconfig env)`).

**Phase 2 — `deploy.yml`** ⬜
- ⬜ `test` job: 3x `dotnet test <sln>` + 1x `dotnet test components/ShardRouting.Tests/ShardRouting.Tests.csproj`.
- ⬜ `check-cluster` job: resolve environment/tag, `aws eks describe-cluster --name microservice1-<environment>`, output `exists`.
- ⬜ `deploy` job (`if: needs.check-cluster.outputs.exists == 'true'`): AWS OIDC auth, `extractions/setup-just`, `azure/setup-kubectl`, `azure/setup-helm`, then the `just` steps from §3.

**Phase 3 — `destroy.yml`** ⬜
- ⬜ Manual dispatch, confirm-by-typing-environment-name gate, `update-kubeconfig` + `destroy-app`.

**Phase 4 — `rollback.yml`** ⬜
- ⬜ Manual dispatch, same confirm gate, `update-kubeconfig` + `rollback`.

**Phase 5 — Validation** ⬜
- ⬜ `actionlint` (or equivalent) against all 3 workflow files for YAML/expression-syntax errors before ever pushing.
- ⬜ Trigger `deploy.yml` via `workflow_dispatch` against `develop` with the cluster live; confirm it reaches `deploy-app` successfully (or, if the cluster's currently torn down, confirm `check-cluster` skips cleanly rather than failing).
- ⬜ Trigger `rollback.yml` after at least 2 Helm revisions exist, confirm it actually reverts.
