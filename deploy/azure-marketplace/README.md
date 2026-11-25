# Symphony for Azure Marketplace

This directory generates an Azure Application **solution template** for a single repository per isolated instance. Marketplace installs the Azure infrastructure and starts Symphony without an interactive shell. The existing dashboard and API sit behind TLS and the customer's single-tenant Entra sign-in. The service starts automatically; a job still needs all configured labels and an existing Symphony managed lease.

## What the template creates

A standard public IP with a predictable Azure DNS name, a dedicated network and security group, and one Ubuntu VM with a managed OS disk. Only ports 80 (certificate issuance and redirect) and 443 (HTTPS) are reachable externally. No SSH, orchestration port, Docker socket or authentication service port is published. The VM runs Caddy, OAuth2 Proxy and the existing Symphony container. OAuth2 Proxy gates both browser sessions and signed Entra bearer tokens. The operator group is checked for both. The package takes identity and Key Vault **references**, never token values, and does not create roles in the tenant.

SQLite and Git worktrees stay on local managed-disk-backed storage, not an Azure Files network share. The plan intentionally avoids autoscaling. Deploy a new isolated instance with distinct credentials and state for each repository. The OS disk is detached on VM deletion; arrange backups and disk cleanup as part of your retention policy. A VM, managed disk and public IP incur Azure charges.

## Before installing

1. Choose an Azure public-cloud region and a globally available DNS label within that region. The deployment will use `https://<label>.<region>.cloudapp.azure.com/`. Use a new resource group and substitute the actual region programmatic name (for example `australiaeast`).
2. In that tenant, [register a single-tenant web app](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app). Register the exact HTTPS **Web** callback `https://<label>.<region>.cloudapp.azure.com/oauth2/callback`. No wildcard, common tenant, public client flow or delegated Microsoft Graph mail/file access is needed for the web app.
3. Create a small security group of **trusted Symphony operators**, assign it to the Enterprise Application and set **Assignment required**. Configure the app to [emit group object IDs in both ID and access tokens](https://learn.microsoft.com/en-us/entra/identity/hybrid/connect/how-to-connect-fed-group-claims). Prefer groups assigned to the application (`ApplicationGroup`); put operators directly in the group. If Entra omits that group claim, the gateway refuses access rather than querying Graph or falling back to broad tenant access. The group object ID is a required deployment input. All group members can use the operator APIs; this is not a separate viewer/admin RBAC system.
4. If using the API from a client, expose a delegated scope on the Symphony registration and configure an approved client to request it, following [Microsoft’s API registration guide](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-configure-app-expose-web-apis). Set the resource's requested access-token version to 2. The gateway checks issuer, audience, expiry, signature and operator group membership; it does not implement separate permission per OAuth scope. Do not enable an app-only client-credentials path for this plan. Use the existing browser session for the same-origin dashboard APIs.
5. Provision a user-assigned identity. Give the VM identity permission to read **only** the four required Key Vault secrets; do not give it Azure management or additional Key Vault permissions. Ensure the vault data endpoint is reachable from the VM. Supply versioned references for controlled rollouts, or unversioned references when you intend a restart to pick up the latest values. The deploying principal needs permission to attach the identity.
   - A fine-grained GitHub token scoped to the configured repository with Issues and Contents read permissions. Do not start with repo write or Actions administration permissions.
   - An existing approved Codex API key. A dedicated budgeted project is recommended. The plan neither creates a key nor grants a subscription. Confirm the pinned Codex CLI can authenticate in the isolated container against your approved project.
   - The Entra web application client secret.
   - A distinct high-entropy managed lease signing secret of at least 32 characters, known to the independently authorised signing controller. Do not reuse the GitHub or Codex secret. The guest bootstrap does not log secret values.
6. Use tested Linux/amd64 image **digests** for Symphony (built from this fork's Dockerfile), Caddy 2 and OAuth2 Proxy v7.15.6 or later supported v7. Use approved pullable repositories without credentials embedded in image URIs. For example, build with `docker build --platform linux/amd64 -t <approved-registry>/symphony:<release> .` and use the repository digest from your authorised image publication. Supplying a digest does not validate the publisher; independently verify provenance, scan findings and licence/Marketplace policy before publication. Private-registry login is not implemented in this plan. No image or Marketplace offer is published by this package.

## Configure and package

From the repository root:

```sh
cd deploy/azure-marketplace
python3 -m unittest -v
python3 package.py --out ../../../symphony-marketplace-build
```

The ZIP has `mainTemplate.json` and `createUiDefinition.json` at its root. Run Microsoft's [ARM template test toolkit](https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/test-toolkit) and the [Create UI Definition sandbox](https://learn.microsoft.com/en-us/azure/azure-resource-manager/managed-applications/test-createuidefinition). Validate the template with `az deployment group validate` and test a disposable customer-tenant deployment before submitting the ZIP under the Partner Center [Azure Application solution template plan](https://learn.microsoft.com/en-us/partner-center/marketplace-offers/plan-azure-app-solution-template). Deployment parameters contain no secret material. Run Command waits for engine health and an anonymous HTTPS 401 through the gateway. A failed startup fails provisioning; check the command's instance view and VM systemd logs.

Set the GitHub owner, repository, branch, labels, polling interval, turn and stall timeouts and concurrency in the Azure wizard. Labels are **AND** conditions and must include `symphony-approved`; only authorised repository owners should apply it. The workflow snapshot is mounted read-only; remote edits are not enabled. The template does not edit labels, issues, pull requests, or environment access. It rejects a changed configuration on redeployment to an existing instance; for this pilot recreate in a fresh group after draining and taking a tested state backup. Swapping credentials by rotating values at existing unversioned secret references requires an authorised coordinated restart.

Deployment outputs:

| Output | Purpose |
| --- | --- |
| `portalUrl` | Browser dashboard, redirects to tenant sign-in when signed out |
| `apiBaseUrl` | Prefix for Symphony v1 APIs, authenticated browser session or approved Entra bearer |
| `healthUrl` | Protected API health, not an anonymous health page |
| `entraRedirectUri` | Exact Entra Web callback |
| `vmName` | VM operator diagnostics and recovery |

## Admission and security boundaries

- Initial boot starts the service, not a job. Use an independently authorised managed lease controller to issue and renew signed, generation-bound leases through the protected management API. This package does not provision, impersonate or start that controller. Stop its renewals and use the existing pause/drain procedure for a planned shutdown. No approval is inferred from installing the package. The managed status API reports lease state; existing Symphony management semantics are unchanged.
- The provided default workflow is for routine source-code maintenance in a trusted repository, with workspace-write sandboxing, no job-provided install hooks and no automatic merge/deploy policy. The contained engine and its child processes are not a hard hostile-code multi-tenant boundary. In particular the engine must resolve the signing key, Docker host administrators can inspect container configuration, and agents run inside the engine container. Do not share the VM, credentials, trust domain or execution labels with untrusted tenants or unreviewed issues. A hostile-workload offering needs separately isolated agent runtime and a separate verifier or signing trust boundary.
- The VM identity is used by the host to read Key Vault; Docker forwarding to IMDS is blocked before any container starts. No host identities, privileged containers or host-mounted Docker control are provided to the worker. Keep the existing network and ingress rules and do not expose the raw engine listener. Restrict who can run Azure VM Run Command.
- Cross-origin state-changing requests and requests with neither same-origin context nor bearer auth are blocked at ingress. API routes require the gateway's auth even if a caller supplies identity-looking headers. Management API methods are for the approved group only and a lease renewal still requires the Symphony signature. Do not publish a no-auth status API.
- This is a pilot package, not evidence of a tested Azure install or Microsoft certification. Verify anonymous portal redirect, anonymous API 401, correct signed Entra cookie and bearer access, wrong issuer/audience and missing-group denial, failure of cross-origin mutation, absence of ports 22/8080, managed identity isolation, private repo clone, Codex Linux sandbox support, idle boot without lease, expiry/pause/drain, and managed-disk persistence across controlled reboot. Run tests with a disposable non-production repo, test credentials and synthetic issues only. Keep the offer unpublished until those checks pass.
