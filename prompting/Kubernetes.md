# EKS Deployment — Kubernetes Manifests for TransactionGateway / TransactionService / TransactionWorker

## 1. Requirements

- Deploy the 3 .NET services (`TransactionGateway`, `TransactionService`, `TransactionWorker`) to the demo EKS cluster described in `KEDA.md`/`Terraform.md`.
- Manifests live in `D:\git\microservice_1\kubernetes` (application repo, not the Terraform repo — same separation-of-concerns rationale as `KEDA.md` §8).
- The cluster needs to reach: ElastiCache (Redis), DynamoDB, SQS, and PostgreSQL (RDS).

> **Correction (2026-08-01, round 1):** §2's original "Service type" row and §3/§4 below incorrectly stated all 3 services are `ClusterIP`-only with no external access. That's wrong — `TransactionGateway.md` §2-§3 has always specified `TransactionGateway` receives traffic via an ALB/`Ingress` ("the ALB is created via the AWS Load Balancer Controller from a Kubernetes `Ingress`"; only `TransactionService` is documented as having "no ALB/Ingress"). Caught when asked why no `ingress.yaml` existed, alongside two follow-on gaps this exposed: no `/health` endpoint existed for the ALB's target group health check, and `main.tf`'s `eks_alb_controller`/`eks_cluster_autoscaler` modules only provision IAM/IRSA — the actual Helm-installed controllers were never scripted, so the Ingress would have sat unprocessed forever even once applied. All three fixed.
>
> **Additions (2026-08-01, round 2):** asked to study the rest of `microservice_0/kubernetes` for further gaps against this reference project's own conventions. Found and fixed: no readiness/liveness/startup probes anywhere; no `db-init` Job (the same "migration tooling not yet built" gap `database.md` §11 already documents, patched only once by hand this session via a throwaway script); no `PodDisruptionBudget`/`HorizontalPodAutoscaler` on `transaction-gateway`/`transaction-service`; bare `app:` labels instead of the standard `app.kubernetes.io/{name,part-of,component}` set. Also found and fixed a real regression risk while wiring up probes: `TransactionService.Grpc`'s Kestrel was HTTP/2-only (needed for gRPC over h2c) on its only port, which would have silently rejected Kubernetes' plain-HTTP/1.1 probe requests — fixed with a second, dedicated health-only port (8081, HTTP/1.1), matching `microservice_0`'s own `dataaccess`/`management` services' `grpc`+`health` dual-port pattern exactly. Verified via Docker that both ports now behave correctly. See §2/§4/§5 below.
>
> **Fix (2026-08-01, round 3):** after the user did a full fresh `terraform apply` (EKS/IRSA/KEDA/DMS all live), `aws ssm get-parameter` calls against confirmed-existing SSM parameters kept failing with `ParameterNotFound` - initially misdiagnosed as a real IAM/propagation issue (wrongly reported as such before this was caught). CloudTrail revealed the true cause: Git Bash (MSYS2) was silently rewriting `--name "/microservice1/develop/..."` into a Windows path (`D:/Program Files/Git/microservice1/develop/...`) before `aws.exe` ever saw it - a well-known MSYS path-mangling quirk, nothing wrong with AWS/IAM/the parameters themselves. Since every `kubernetes/scripts/*.sh` that reads from SSM/Secrets Manager would hit the same bug if ever run from Git Bash instead of WSL, added `export MSYS_NO_PATHCONV=1` (harmless on WSL/Linux/macOS) to all 6 affected scripts as a defensive fix, not just a one-off diagnostic workaround. Verified fixed via direct `aws ssm get-parameter` call.

## 2. Decisions

| Decision | Choice | Why |
|---|---|---|
| EKS cluster/nodegroup/IRSA Terraform | **Re-enable now** — currently fully commented out in `main.tf` under "EKS disabled to save cost" | These manifests are meaningless without a real cluster to apply them to; deferring further would just split this work into two disconnected passes. Real hourly cost (control plane + EC2 nodes) starts accruing once applied — an explicit, deliberate choice, not a default. |
| IAM vs. Kubernetes ServiceAccount split | **IRSA's IAM half (the role + trust policy) is Terraform's job; the ServiceAccount object (with the `eks.amazonaws.com/role-arn` annotation) is this doc's job** | Standard IRSA split. The 3 IAM roles (`irsa_transaction_service`, `irsa_transaction_worker`, `irsa_keda_trigger_auth`) already exist in `main.tf`, commented out alongside the rest of the EKS block — no new IAM policy design needed, just re-enabling + a matching `ServiceAccount` manifest per role. |
| ElastiCache / PostgreSQL access | **No IAM/service-account involved** — plain username/password auth, gated by security group (already permissive to the VPC CIDR) | Neither uses IAM authentication in this project (`AuthTokenEnabled: false` on ElastiCache; standard Postgres auth on RDS). Once pods run inside the VPC's private subnets, network reachability is already satisfied by the existing security group rules — nothing new to provision for connectivity itself. |
| DB/Redis credential delivery | **`scripts/apply-db-secret.sh` pulls live from Secrets Manager** (`ssm-outputs.tf`'s `db-{shard-1,reporting}-connection-string` entries), pipes into `kubectl create secret ... --dry-run=client -o yaml \| kubectl apply -f -` | Upgraded from an earlier "manually fill in a template file" plan once `microservice_0/kubernetes/scripts/apply-db-secret.sh` was found and studied — same pattern, no operator/IRSA role needed (script runs with the developer's own local AWS creds, not from inside the cluster), no real secret value ever touches disk or git. |
| Container images | **Re-apply the `ecr` module, then build + push real images via `scripts/build-and-push-images.sh`** | The 3 ECR repos referenced in an earlier `terraform output` don't actually exist in AWS (confirmed via `aws ecr describe-repositories` — likely drifted since a prior destroy). `module "ecr"` itself was never commented out, so a plain `terraform apply` should recreate them. |
| Image tagging | **Git short SHA** (`git rev-parse --short HEAD`), not `latest` — matches `microservice_0`'s convention exactly | Every build is independently addressable/rollback-able instead of overwriting the same floating tag. Both `just build-push` and `just deploy-app` default `tag` to the current commit's SHA so a plain `just deploy-all` always deploys exactly what's checked out. |
| KEDA autoscaling | **In scope now** — re-enable `module "keda"` (Helm release) and write `ScaledObject`/`TriggerAuthentication` manifests for `TransactionWorker` | `KEDA.md`'s entire design (claim-check-with-lease, `minReplicaCount: 0` safety, §5/§6) is pointless without KEDA actually installed and wired up — deferring it would leave the system's core scaling behavior undemonstrated. |
| Service type | `transaction-gateway`: `ClusterIP` + `Ingress` (ALB, internet-facing); `transaction-service`/`transaction-worker`: `ClusterIP` only, never exposed | `TransactionGateway.md` §2-§3: the Gateway is the system's one intended external entry point ("Receives HTTP requests from an ALB"); only `TransactionService` is documented as internal-only. `TransactionWorker` has no HTTP listener at all. |
| ALB controller / cluster-autoscaler install | `main.tf`'s modules only provision IAM/IRSA — the controllers themselves are installed via `scripts/install-{alb-controller,cluster-autoscaler}.sh` (Helm), chained into `just deploy-all` | Same split `microservice_0/kubernetes` uses (no first-party Terraform resource for either controller). Without this, `transaction-gateway/ingress.yaml` would sit unprocessed forever, and `node_max_size=8` would be an unused ceiling nothing ever scales into. |
| ExternalDNS | **Not installed** — `main.tf`'s `eks_external_dns` module's IRSA role exists but sits unused | Unlike `microservice_0` (`ekslab.xyz`), this project has no Route53 domain/hosted zone. Installing ExternalDNS with no `domainFilters` would let it manage *any* zone visible to its IAM permissions in this shared AWS account — not safe to do blindly. `transaction-gateway`'s Ingress is reachable via the ALB's own auto-generated DNS name instead; add a real installer if a domain is ever wired up. |
| Health check endpoints | `GET /health`, `/ready`, `/live` (all return 200, no downstream dependency checks) added to both `TransactionGateway.Api` and `TransactionService.Grpc`'s `Program.cs` | Matches `microservice_0/kubernetes`'s `startupProbe`/`readinessProbe`/`livenessProbe` convention exactly. `TransactionWorker` gets none - it has no HTTP listener at all (`dotnet/runtime` base image, no Kestrel), so there's nothing to probe. |
| TransactionService's health port | **Separate Kestrel endpoint**: `8080` stays `Protocols: Http2` (gRPC/h2c only), a new `8081` is `Protocols: Http1` (health only) - two named `Kestrel:Endpoints` in `appsettings.json`, `ASPNETCORE_URLS` removed from the Dockerfile so it can't fight the explicit config | Found while wiring up probes: a single port can't do both safely here. Tried `Http1AndHttp2` on one port first - Docker logs showed Kestrel refusing to enable HTTP/2 at all without TLS ("HTTP/2 is not enabled... Connections to this endpoint will use HTTP/1.1"), which would have silently broken gRPC entirely. Reverted, and instead matched `microservice_0`'s `dataaccess`/`management` services' own `grpc`(8080)+`health`(8081) dual-port pattern - verified via Docker: 8081 serves plain HTTP/1.1 `/health` (200 OK), 8080 correctly still rejects HTTP/1.1 ("HTTP/1.x request sent to an HTTP/2 only endpoint"), unchanged from before. |
| `transaction-gateway`/`transaction-service` replica strategy | `replicas: 2` + `HorizontalPodAutoscaler` (CPU, 70% target, 2-5) + `PodDisruptionBudget` (`minAvailable: 1`) | Matches `microservice_0`'s `gateway`/`dataaccess`/`management` exactly, chosen over keeping `replicas: 1` (cheaper but no redundancy, and a PDB would be actively harmful at `replicas: 1` - it'd block all voluntary node drains). `transaction-worker` gets neither - KEDA's `ScaledObject` already owns its replica count, floor of 0. |
| `db-init` Job | Kubernetes `Job` + `ConfigMap` (published from `components/TransactionWorker/resources/postgres/*.sql`) that **drops and recreates** `transactions`/`transactions_reporting` on every `just deploy-all`, run via `scripts/init-db.sh` | Matches `microservice_0/kubernetes/db-init`'s exact behavior and rationale (destructive by design, for this demo - keeps schema definition and deploy in lockstep, no drift possible). Closes the same gap `database.md` §11 Phase 2 already flags as not-yet-built; previously only patched once by hand via a throwaway script this session. |
| Resource labels | Standard `app.kubernetes.io/{name,part-of,component}` set on every resource, replacing a bare `app: <name>` label | Matches `microservice_0/kubernetes` exactly; needed for `PodDisruptionBudget`/`HorizontalPodAutoscaler` selectors anyway, and keeps selector/label conventions consistent for any future tooling (dashboards, `kubectl` filtering) that expects it. |
| Manifest format | Plain Kubernetes YAML (no Helm chart, no Kustomize) + `envsubst` placeholders for per-environment values | Consistent with this project's general preference for the direct/explicit option over an added abstraction layer (e.g. plain RDS over Aurora elsewhere) — matches `microservice_0/kubernetes`'s own approach exactly (its sibling `Helm/` directory is the Helm-based alternative there; this project only needs the plain-manifest path). |
| Deploy automation | **`just` + shell scripts**, mirroring `microservice_0/kubernetes/justfile` and `microservice_2_terraform/justfile`'s established convention | This user already has a consistent `just`-based workflow across `microservice_0`/`microservice_2` (and `microservice_1_terraform` itself already has one) — matching it directly rather than inventing a new pattern. All scripts read from SSM Parameter Store/Secrets Manager only, zero dependency on the Terraform CLI, its state, or S3 backend credentials — so the app-deploy pipeline can run independently of whoever/whatever last ran `terraform apply`. |

## 3. Architecture Overview

```
   HTTP client ───► ALB (AWS Load Balancer Controller, from transaction-gateway's Ingress)
                                          │
                              EKS cluster (namespace: microservice1-develop)
                    ┌─────────────────────┼────────────────────────────────────────────┐
                    │                     ▼                                            │
                    │  ┌────────────────────┐        ┌─────────────────────┐  │
                    │  │ transaction-gateway│  gRPC  │ transaction-service │  │
                    │  │ Deployment + SVC   │───────►│ Deployment + SVC     │  │
                    │  │ (no ServiceAccount  │        │ SA: irsa_transaction │  │
                    │  │  needed - no AWS    │        │     _service (SQS)   │  │
                    │  │  API calls)         │        └──────────┬──────────┘  │
                    │  └─────────┬──────────┘                   │             │
                    │            │ Redis (gw-cache)              │ Redis(svc) │
                    │            │                                │ Postgres  │
                    │  ┌─────────▼──────────┐        ┌───────────▼─────────┐  │
                    │  │ transaction-worker │        │  KEDA ScaledObject   │  │
                    │  │ Deployment          │◄───────│  (aws-sqs-queue,     │  │
                    │  │ SA: irsa_transaction│        │   TriggerAuth: SA    │  │
                    │  │     _worker (SQS +  │        │   irsa_keda_trigger_ │  │
                    │  │     DynamoDB)       │        │   auth)              │  │
                    │  └─────────┬──────────┘        └──────────────────────┘  │
                    │            │ Postgres                                    │
                    └────────────┼────────────────────────────────────────────┘
                                 │
          ┌──────────────┬──────┴──────┬──────────────┬─────────────────┐
          ▼              ▼             ▼               ▼                 ▼
   ElastiCache      ElastiCache    DynamoDB          SQS            RDS PostgreSQL
   gw-cache         svc-cache   transaction-claims  TransactionQueue  shard-1 / reporting
   (Redis, no IAM)  (Redis,no   (IAM via IRSA)      (IAM via IRSA)   (username/password,
                     IAM)                                              SG-gated)
```

- `transaction-gateway` is the system's one external entry point — a `ClusterIP` Service fronted by an `Ingress` (`ingressClassName: alb`), which the AWS Load Balancer Controller turns into a real internet-facing ALB. It needs no `ServiceAccount`/IRSA itself — it only talks to Redis (network-gated) and `transaction-service` over gRPC (in-cluster).
- `transaction-service` and `transaction-worker` are `ClusterIP`-only (or no Service at all, for the worker) — never reachable from outside the cluster. Each gets a dedicated `ServiceAccount` annotated with their respective IRSA role ARN (Terraform output).
- KEDA's `TriggerAuthentication` uses its own `ServiceAccount`/IRSA role (`irsa_keda_trigger_auth`) — scoped to `sqs:GetQueueAttributes` only, separate from the worker's own broader SQS permissions.

## 4. Manifest Layout

```
kubernetes/
├── justfile                              # just update-kubeconfig / install-alb-controller / install-cluster-autoscaler / init-db / build-push / deploy-app / deploy-all / destroy-app
├── scripts/
│   ├── update-kubeconfig.sh              # aws eks update-kubeconfig, cluster name from SSM
│   ├── install-alb-controller.sh         # Helm install - main.tf's module only provisions IAM/IRSA
│   ├── install-cluster-autoscaler.sh     # Helm install - main.tf's module only provisions IAM/IRSA
│   ├── build-and-push-images.sh          # docker build+push all 3 images to ECR, tag = git short SHA
│   ├── apply-db-secret.sh                # pulls Postgres connection strings from Secrets Manager -> kubectl secret
│   ├── init-db.sh                        # publishes db-init-scripts ConfigMap, runs db-init Job, waits for completion
│   ├── deploy-app.sh                     # envsubst manifests with real SSM values, kubectl apply
│   └── destroy-app.sh                    # kubectl delete the app layer + uninstall the 2 Helm releases
├── db-init/
│   └── job.yaml                          # drops + recreates transactions/transactions_reporting - destructive by design
├── transaction-gateway/
│   ├── deployment.yaml                   # ${NAMESPACE}/${GATEWAY_IMAGE}/${GW_REDIS_ENDPOINT} - envsubst placeholders; replicas: 2, probes
│   ├── service.yaml                      # ClusterIP
│   ├── ingress.yaml                      # ALB, internet-facing, plain HTTP (no domain provisioned - see §2)
│   ├── hpa.yaml                          # CPU 70%, 2-5 replicas
│   └── pdb.yaml                          # minAvailable: 1
├── transaction-service/
│   ├── deployment.yaml                   # ${NAMESPACE}/${SERVICE_IMAGE}/${SVC_REDIS_ENDPOINT}/${SQS_QUEUE_URL}; replicas: 2, probes, dual-port (8080 grpc/8081 health)
│   ├── service.yaml                      # ClusterIP
│   ├── hpa.yaml                          # CPU 70%, 2-5 replicas
│   └── pdb.yaml                          # minAvailable: 1
└── transaction-worker/
    ├── deployment.yaml                   # replicas: 0 - KEDA's ScaledObject owns replica count; no probes, no HTTP listener
    ├── scaledobject.yaml                 # keda.sh/v1alpha1, from KEDA.md §6
    └── triggerauthentication.yaml        # UNVALIDATED - see §5 Phase 2 note
```

- `components/TransactionWorker/resources/postgres/{01-shard-schema,02-reporting-schema}.sql` — the actual DDL `db-init` applies (from `database.md` §6/§7), kept alongside `TransactionWorker` as the closest analog to "owns the write path," even though `transactions_reporting` isn't written by any app code directly (fed by DMS CDC) and is read by `TransactionService`, not `TransactionWorker`.

- **No `namespace.yaml` or `serviceaccount.yaml` files** — discovered while writing these that Terraform's `irsa-service-role` module (`modules/irsa-service-role/main.tf`) already creates the `kubernetes_service_account` resource directly (via the `kubernetes` provider), annotated with `eks.amazonaws.com/role-arn`, as part of applying each `irsa_*` module. `kubernetes_namespace.app` in `main.tf` likewise already creates the namespace. §2's "IRSA split" decision undersold this — Terraform owns *both* halves (IAM role and the K8s ServiceAccount object), not just the IAM half. Deployments just reference `serviceAccountName: transaction-service`/`transaction-worker` and trust that Terraform already created them correctly.
- **No `secrets/` directory or template file either** — superseded by `scripts/apply-db-secret.sh` pulling live from Secrets Manager (see §2's credential-delivery decision); nothing to template or git-ignore since no real secret value is ever written to disk.
- Every environment-specific value (image URLs, Redis/RDS/SQS/DynamoDB endpoints, namespace) is an `envsubst` `${PLACEHOLDER}` in the YAML, not a hardcoded value — `scripts/deploy-app.sh` resolves all of them from SSM Parameter Store at deploy time and pipes each manifest through `envsubst` before `kubectl apply -f -`. Verified via a dry-run substitution with dummy values (all 7 templated files produce valid YAML with no leftover `${...}`).
- `TransactionService__GrpcAddress` in `transaction-gateway/deployment.yaml` is the only hardcoded in-cluster value (`http://transaction-service:8080`) — it's a same-cluster Service DNS name, not an environment-specific endpoint, so it doesn't need to come from SSM.
- Both `Shards__ConnectionStrings__{0,1,2}` env vars in `transaction-service`/`transaction-worker` pull from the same `shard-connection-string` Secret key, matching the current collapsed single-physical-shard topology (`database.md` §1).

## 5. Implementation Plan

**Phase 1 — Terraform: re-enable EKS + IRSA + KEDA + reconcile ECR** ✅ done and applied (2026-08-01)
- ✅ Uncommented `provider "kubernetes"`/`provider "helm"`, `module "eks_cluster"`, `module "eks_nodegroup"`, `module "eks_ebs_csi"`, `module "eks_alb_controller"`, `module "eks_external_dns"`, `module "eks_cluster_autoscaler"`, `kubernetes_namespace "app"` in `main.tf`.
- ✅ Uncommented `module "irsa_transaction_service"` (SQS `SendMessage` only), `module "irsa_transaction_worker"` (SQS receive/delete/GetQueueAttributes + DynamoDB `PutItem`/`UpdateItem`/`Query`), `module "irsa_keda_trigger_auth"` (SQS `GetQueueAttributes` only) and their IAM policy documents — matches this doc's §3 architecture exactly.
- ✅ Uncommented `module "keda"` (Helm release of the KEDA core chart).
- ✅ Bumped node sizing: `node_desired_size` 2→4, `node_max_size` 4→8 on `t3.small` — sized for the ~10 baseline add-on pods (CoreDNS, EBS CSI controller, KEDA, ALB controller, external-dns, cluster-autoscaler) plus the 3 app services plus `TransactionWorker`'s KEDA burst to 5 replicas, since `t3.small`'s 8-pods/node ceiling only leaves 5 schedulable slots/node after daemonsets.
- ✅ `terraform apply` run by the user (fresh apply, 110 resources added, 0 destroyed - DMS CDC pipeline included). Verified independently via AWS CLI/kubectl, not just trusted from the apply log: EKS `ACTIVE` (`v1.31`), 4 nodes `Ready`, namespace `microservice1-develop` `Active`, KEDA operator+webhooks+metrics-apiserver running in the `keda` namespace, all 3 ServiceAccounts (`transaction-service`/`transaction-worker`/`keda-trigger-auth`) exist with correct `eks.amazonaws.com/role-arn` annotations, all 3 ECR repos exist, all 4 Secrets Manager DB connection-string entries exist, all 13 SSM parameters exist (see round 3's note above for the Git-Bash false-alarm detour getting to that last confirmation).

**Phase 2 — Manifests + deploy automation** ✅ code written / ⬜ validated against a live cluster
- ✅ `transaction-gateway/` Deployment (`replicas: 2`, `startupProbe`/`readinessProbe`/`livenessProbe`) + Service (`ClusterIP`) + `Ingress` (ALB, internet-facing) + `HorizontalPodAutoscaler` + `PodDisruptionBudget`. `GET /health`, `/ready`, `/live` added to `Program.cs`.
- ✅ `transaction-service/` Deployment (`replicas: 2`, same 3 probes, references `serviceAccountName: transaction-service`) + Service (`ClusterIP`) + `HorizontalPodAutoscaler` + `PodDisruptionBudget`. `GET /health`, `/ready`, `/live` added to `Program.cs`, served on a **new dedicated port 8081** (`Protocols: Http1`) separate from gRPC's port 8080 (`Protocols: Http2`) - see §2's Kestrel decision row for why a single port can't do both. `Dockerfile` updated (`EXPOSE 8081`, `ASPNETCORE_URLS` removed in favor of explicit `Kestrel:Endpoints`); local `docker-compose.yml` updated with the extra port mapping.
- ✅ `transaction-worker/` Deployment (`replicas: 0`, references `serviceAccountName: transaction-worker`, no probes/Service - no HTTP listener) + `ScaledObject` + `TriggerAuthentication`.
- ✅ `db-init/job.yaml` + `scripts/init-db.sh` + `components/TransactionWorker/resources/postgres/{01-shard-schema,02-reporting-schema}.sql`, wired into `deploy-app.sh` right after `apply-db-secret.sh`. Destructive by design (drops + recreates both tables every deploy), matching `microservice_0`'s own `db-init` behavior.
- ✅ All resources carry the standard `app.kubernetes.io/{name,part-of,component}` label set (replacing a bare `app:` label), named container ports (`http`/`grpc`/`health` instead of raw numbers), explicit `imagePullPolicy: IfNotPresent`.
- ✅ All Deployments/`Ingress`/`ScaledObject`/`HPA`/`PDB`/`db-init Job` use `envsubst` `${PLACEHOLDER}`s for image URLs/endpoints/namespace instead of hardcoded values.
- ✅ `justfile` + `scripts/{update-kubeconfig,install-alb-controller,install-cluster-autoscaler,build-and-push-images,apply-db-secret,init-db,deploy-app,destroy-app}.sh`, mirroring `microservice_0/kubernetes`'s established convention exactly - everything reads from SSM Parameter Store/Secrets Manager, zero Terraform CLI/state dependency. `just deploy-all` chains `install-alb-controller` → `install-cluster-autoscaler` → `build-push` → `deploy-app` (which itself calls `apply-db-secret` → `init-db` before applying manifests).
- ✅ `microservice_1_terraform/justfile`'s `destroy` recipe updated with the same "run `destroy-app` first" warning `microservice_0/terraform/justfile` has - previously missing here since there was no ALB to cause that problem until now.
- ✅ All 13 templated YAML files confirmed to substitute cleanly via a dry-run `envsubst` pass with dummy values (valid YAML out, no leftover `${...}`); all 8 scripts pass `bash -n`; `apply-db-secret.sh`'s Secrets-Manager-format-to-Npgsql-format reformatting tested against a realistic sample string.
- ✅ **Caught and fixed a real regression via Docker before it shipped**: first attempt at `TransactionService`'s health port used a single port with `Protocols: Http1AndHttp2` - Docker logs showed this silently disables HTTP/2 entirely without TLS ("Connections to this endpoint will use HTTP/1.1"), which would have broken gRPC. Switched to the dual-port design and verified via Docker: port 8081 serves plain HTTP/1.1 `/health` → `200 OK`; port 8080 still correctly rejects HTTP/1.1 ("HTTP/1.x request sent to an HTTP/2 only endpoint") - unchanged gRPC behavior. `TransactionGateway` rebuilt clean (26/26 tests pass); `TransactionService`'s local `dotnet build` was blocked by a leftover locally-running process holding its DLLs/PDBs (unrelated to the code change - not stopped, since it wasn't started by this session's automation) but the Docker build/run (the more representative path) succeeded cleanly.
- ⬜ `kubectl apply --dry-run=client` couldn't go further than plain YAML syntax - it needs live API discovery even in dry-run mode, and the cluster doesn't exist yet.
- ⚠️ **Real design gap found, not yet resolved**: `transaction-worker/triggerauthentication.yaml` uses KEDA's default `podIdentity` (the KEDA operator's own pod identity, in the separate `keda` namespace) since that's the only option that works while `transaction-worker` is scaled to 0 replicas — but `main.tf`'s `irsa_keda_trigger_auth` module's trust policy is scoped to a *different*, dedicated `keda-trigger-auth` ServiceAccount living in the app namespace, which KEDA's `podIdentity` has no direct way to target. See the file's own header comment for the two ways to resolve this (retarget the IRSA trust policy to the KEDA operator's actual ServiceAccount, or fold its permission into `transaction-worker`'s own broader role and use `identityOwner: workload` instead). Needs a decision + live-cluster testing before this piece can be trusted.

**Phase 3 — Validation** ⬜ (not started, blocked on Phase 1's `terraform apply`)
- ⬜ `just deploy-all`; confirm `transaction-gateway`/`transaction-service` Deployments reach `Ready` (all 3 probes passing) with 2/2 replicas each.
- ⬜ Confirm `db-init` actually completes successfully against the real shard-1/reporting instances (`kubectl logs job/db-init`).
- ⬜ Confirm the AWS Load Balancer Controller actually provisions an ALB for `transaction-gateway`'s `Ingress` and its target group shows healthy (`GET /health`); hit it externally via `kubectl get ingress -n microservice1-develop transaction-gateway` for the ALB hostname.
- ⬜ Submit a transaction through that external ALB endpoint; confirm it flows through to SQS → `transaction-worker` → Postgres, same as the local Docker Compose validation already done.
- ⬜ Confirm the HPAs actually react to load (or at minimum report a valid current CPU utilization, proving the metrics pipeline works) and the PDBs don't block a routine `kubectl drain`.
- ⬜ Confirm KEDA scales `transaction-worker` replicas with queue depth (`KEDA.md` §9's demo-validation phase) - resolve the `TriggerAuthentication` design gap above first, or this step will just fail.
- ⬜ Confirm Cluster Autoscaler actually adds nodes under load (e.g. once `TransactionWorker` bursts toward 5 replicas and the node group fills past `node_desired_size=4`).
