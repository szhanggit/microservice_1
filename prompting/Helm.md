# Helm Chart — TransactionGateway / TransactionService / TransactionWorker

## 1. Requirements

- Add a Helm chart at `D:\git\microservice_1\helm` that deploys the same three services `kubernetes/` already deploys via plain manifests + `envsubst`: `TransactionGateway`, `TransactionService`, `TransactionWorker`, plus their supporting objects (`Service`, `HorizontalPodAutoscaler`, `PodDisruptionBudget`, `Ingress`, KEDA's `ScaledObject`/`TriggerAuthentication`).
- Add a `justfile` alongside the chart, mirroring `kubernetes/justfile`'s workflow.
- Planning stage only as of this doc's last update — no chart files, `justfile`, or directory have been created yet. This doc records the design decided before any of it is written.

## 2. Decisions

| Decision | Choice | Why |
|---|---|---|
| Chart structure | **One chart, all three services** — a single `Chart.yaml`/`values.yaml`/`templates/` covering `transaction-gateway`/`transaction-service`/`transaction-worker`, each block toggleable via `values.yaml` (e.g. `gateway.enabled`) | Simplest to version and deploy as one unit; matches how `deploy-app.sh` already treats all three as always-deployed-together. Considered and rejected: an umbrella chart with 3 subcharts, and 3 fully independent charts — both add scaffolding/complexity this project's single-environment-at-a-time deploy cadence doesn't need. |
| Relationship to `kubernetes/` | **Coexist, not replace** | `kubernetes/` (raw manifests, its own `justfile`, `envsubst`-based scripts) stays exactly as-is and keeps working. `helm/` is an independent, parallel deployment path — not a migration. Nothing in `kubernetes/` is touched by this work. |
| Dynamic values (image tags, Redis/SQS/DynamoDB endpoints) | **`helm/justfile` fetches the same SSM Parameter Store values `deploy-app.sh` already reads, and passes them to `helm upgrade --install` via `--set`** | Matches `Kubernetes.md` §2's existing "every environment-specific value is resolved from SSM at deploy time, nothing hardcoded" principle. Rejected: committing static `values-<env>.yaml` files with baked-in image tags/endpoints — those would go stale in git instead of staying live-sourced from Terraform's SSM outputs. |
| Third-party controllers (AWS Load Balancer Controller, cluster-autoscaler, ExternalDNS) | **Out of scope for this chart** — `scripts/install-{alb-controller,cluster-autoscaler,external-dns}.sh` keep running exactly as they do today, independent of `helm/` | These already install their own public Helm charts via existing scripts; folding them into this chart as dependencies would add coupling for no behavioral benefit — this chart's job is the three application services, not cluster add-ons. |
| `db-init` Job | **Stays a separate script step** (`scripts/init-db.sh` or an equivalent under `helm/`), run before `helm upgrade --install`, not a chart-templated resource | Matches today's behavior exactly (destructive schema drop/recreate, `Kubernetes.md` §2's `db-init` decision) — kept as an explicit, visible pre-step rather than folded into a Helm hook, so its destructive nature stays obvious rather than implicit in a release lifecycle hook. |
| Namespace / IRSA ServiceAccounts | **Chart assumes both pre-exist** — no `Namespace` or `ServiceAccount` templates | Matches `Kubernetes.md` §4's existing finding: Terraform's `kubernetes_namespace.app` and each `irsa-service-role` module already create these, annotated with the correct `eks.amazonaws.com/role-arn`. The chart only ever references `serviceAccountName:`, never creates one — avoids any risk of the chart drifting/conflicting with Terraform's ownership of the IRSA annotation. |
| Chart location | **`helm/Chart.yaml` directly** (no nested subdirectory) | Simplest layout for the one chart this repo currently has; matches `kubernetes/`'s own flat top-level layout. |
| `helm/justfile` recipe naming | **Same recipe names as `kubernetes/justfile`** (`update-kubeconfig`, `install-alb-controller`, `install-external-dns`, `install-cluster-autoscaler`, `apply-db-secret`, `init-db`, `build-push`, `deploy-app`, `deploy-all`, `destroy-app`) | The two `justfile`s live in different directories (`cd` into whichever you mean), so there's no actual collision — identical names mean muscle memory transfers directly between the two deployment paths. Only the recipe *bodies* differ (`helm upgrade --install` instead of `envsubst` + `kubectl apply`). |

> **Open question (2026-08-02):** the Ingress's ACM certificate ARN and ExternalDNS hostname (`microservice1.ekslab.xyz`) are currently hardcoded directly in `kubernetes/transaction-gateway/ingress.yaml`, not sourced from SSM. Not yet decided whether the Helm chart should expose these as `values.yaml` entries (with defaults matching what's live today) or just copy them over as-is, hardcoded in the template like today. Resolve before writing `templates/transaction-gateway/ingress.yaml`.

## 3. Chart Layout (proposed, not yet created)

```
helm/
├── Chart.yaml                              # apiVersion v2, name: microservice1
├── values.yaml                             # defaults; per-request values overridden via --set from justfile
├── justfile                                # same recipe names as kubernetes/justfile - see §2
└── templates/
    ├── transaction-gateway/
    │   ├── deployment.yaml                 # gateway.image / gateway.redis.endpoint / gateway.replicaCount
    │   ├── service.yaml                    # ClusterIP
    │   ├── ingress.yaml                    # ALB, hostname/ACM cert ARN - see open question above
    │   ├── hpa.yaml                        # CPU 70%, 2-5 replicas
    │   └── pdb.yaml                        # minAvailable: 1
    ├── transaction-service/
    │   ├── deployment.yaml                 # service.image / service.redis.endpoint / service.sqs.queueUrl
    │   ├── service.yaml                    # ClusterIP
    │   ├── hpa.yaml                        # CPU 70%, 2-5 replicas
    │   └── pdb.yaml                        # minAvailable: 1
    └── transaction-worker/
        ├── deployment.yaml                 # worker.image; replicaCount not set here - ScaledObject owns it
        ├── scaledobject.yaml               # minReplicaCount: 1 (see KEDA.md §6 / this repo's live scale-from-zero finding below)
        └── triggerauthentication.yaml      # provider: aws, identityOwner: keda
```

- No `namespace.yaml`/`serviceaccount.yaml` templates — see §2's Namespace/IRSA decision. `helm upgrade --install` is run with an explicit `--namespace` flag (Terraform-created namespace), not a chart-managed `Namespace` resource.
- No `db-init/` directory under `templates/` — see §2's `db-init` decision.
- `values.yaml` holds the per-service `enabled`/`image`/`replicaCount`/endpoint fields as empty defaults; `helm/justfile`'s `deploy-app`/`deploy-all` recipes populate the environment-specific ones (image URLs, Redis/SQS/DynamoDB endpoints) via `--set` at deploy time, exactly mirroring which values `kubernetes/scripts/deploy-app.sh` currently resolves from SSM.
- `transaction-worker`'s `scaledobject.yaml` should carry over the **live production finding**, not `KEDA.md` §6's original `minReplicaCount: 0` design: this repo's actual EKS cluster currently runs `minReplicaCount: 1` because KEDA 2.16.1's scale-from-zero push-loop was found to reliably stop ticking after its first activation (root cause not identified; not credentials, not queue-depth correctness, not node preemption — see `kubernetes/transaction-worker/scaledobject.yaml`'s header comment for the full investigation). The Helm template should match what's actually deployed, not the original KEDA.md design intent, until that's root-caused.

## 4. Implementation Plan

**Phase 1 — Resolve open question** ⬜
- ⬜ Decide ACM cert ARN / ExternalDNS hostname handling (§2's open question) before writing `ingress.yaml`.

**Phase 2 — Chart scaffold** ⬜
- ⬜ `Chart.yaml`, `values.yaml` with empty/placeholder defaults for every per-environment field.
- ⬜ `templates/transaction-gateway/*`, `templates/transaction-service/*`, `templates/transaction-worker/*`, ported 1:1 from `kubernetes/`'s existing manifests, converting `envsubst` `${PLACEHOLDER}`s to `{{ .Values.* }}` references.

**Phase 3 — `helm/justfile`** ⬜
- ⬜ `update-kubeconfig`, `install-alb-controller`, `install-external-dns`, `install-cluster-autoscaler` recipes — thin wrappers calling the *existing* `kubernetes/scripts/*.sh` (no need to duplicate these; they're not chart-specific).
- ⬜ `apply-db-secret`, `init-db` recipes — same, delegate to existing scripts.
- ⬜ `build-push` recipe — delegate to existing `kubernetes/scripts/build-and-push-images.sh` (image builds aren't Helm-specific either).
- ⬜ `deploy-app`/`deploy-all` recipes — new logic: resolve the same SSM parameters `deploy-app.sh` does, then `helm upgrade --install microservice1 ./helm --namespace <ns> --set ...`.
- ⬜ `destroy-app` recipe — `helm uninstall microservice1 --namespace <ns>`, plus the same Ingress-hostname-capture / `cleanup-dns.sh` / third-party Helm release uninstalls `kubernetes/scripts/destroy-app.sh` already does (still delegated, not duplicated).

**Phase 4 — Validation** ⬜
- ⬜ `helm lint` / `helm template` against dummy values — confirm valid YAML, no leftover unresolved placeholders.
- ⬜ Deploy to the live `develop` EKS cluster alongside (not replacing) the currently-running `kubernetes/`-deployed release; confirm no resource-name collisions (both paths would target the same `Deployment`/`Service` names in the same namespace — needs an explicit decision on whether to test in a scratch namespace or tear down the `kubernetes/`-deployed release first).
- ⬜ Full end-to-end smoke test through the Helm-deployed release (submit a transaction via ALB, confirm it lands in Postgres), matching the validation already done for the `kubernetes/` path.
