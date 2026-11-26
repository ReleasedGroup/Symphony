# Symphony for Azure Marketplace

This directory generates an Azure Application **solution template** for a single repository per isolated instance. Marketplace installs the Azure infrastructure and starts Symphony without an interactive shell. The existing dashboard and API sit behind TLS and the customer's single-tenant Entra sign-in. The service starts automatically; a job still needs all configured labels and an existing Symphony managed lease.

[Marketplace listing copy, logos and certification notes](listing/README.md)

## What the template creates

A standard public IP with a predictable Azure DNS name, a dedicated network and security group, and one Ubuntu VM with a managed OS disk. Only ports 80 (certificate issuance and redirect) and 443 (HTTPS) are reachable externally. No SSH, orchestration port, Docker socket or authentication service port is published. The VM runs Caddy, OAuth2 Proxy and the existing Symphony container. OAuth2 Proxy gates both browser sessions and signed Entra bearer tokens. The operator group is checked for both. The wizard can create a dedicated user-assigned managed identity and vault or use existing references. In both cases the VM receives only secret URIs. It never receives Graph directory permissions.

SQLite and Git worktrees stay on local managed-disk-backed storage, not an Azure Files network share. The plan intentionally avoids autoscaling. Deploy a new isolated instance with distinct credentials and state for each repository. The OS disk is detached on VM deletion; arrange backups and disk cleanup as part of your retention policy. A VM, managed disk and public IP incur Azure charges.

## Before installing

1. Choose an Azure public-cloud region and a globally available DNS label within that region. The deployment will use `https://<label>.<region>.cloudapp.azure.com/`. Use a new resource group and substitute the actual region programmatic name (for example `australiaeast`).
2. In that tenant, [register a single-tenant web app](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app). Register the exact HTTPS **Web** callback `https://<label>.<region>.cloudapp.azure.com/oauth2/callback`. No wildcard, common tenant, public client flow or delegated Microsoft Graph mail/file access is needed for the web app.
3. Create a small security group of **trusted Symphony operators**, assign it to the Enterprise Application and set **Assignment required**. Configure the app to [emit group object IDs in both ID and access tokens](https://learn.microsoft.com/en-us/entra/identity/hybrid/connect/how-to-connect-fed-group-claims). Prefer groups assigned to the application (`ApplicationGroup`); put operators directly in the group. If Entra omits that group claim, the gateway refuses access rather than querying Graph or falling back to broad tenant access. The group object ID is a required deployment input. All group members can use the operator APIs; this is not a separate viewer/admin RBAC system.
4. If using the API from a client, expose a delegated scope on the Symphony registration and configure an approved client to request it, following [Microsoft’s API registration guide](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-configure-app-expose-web-apis). Set the resource's requested access-token version to 2. The gateway checks issuer, audience, expiry, signature and operator group membership; it does not implement separate permission per OAuth scope. Do not enable an app-only client-credentials path for this plan. Use the existing browser session for the same-origin dashboard APIs.
5. Choose one credential mode in **Entra and Key Vault**:
   - **Create dedicated identity and vault** creates a new user-assigned managed identity, a dedicated RBAC-enabled Key Vault, three provider secret records and a Key Vault Secrets User grant scoped to that vault. Azure requires permission to deploy the resources and assign that role. Enter provider-issued values in the masked fields; they are ARM `secureString` inputs to the secret resources, not VM configuration or deployment outputs. Do not enable ARM request/response debugging. Use a fresh resource group; redeploying this mode can create new secret versions. The vault has soft delete and purge protection.
   - **Use existing identity and vault references** accepts the identity resource ID and four secret URIs. Grant the identity read access to just those four secrets; do not grant directory or Azure management access. The deploying principal needs permission to attach it, and the VM must reach the vault's data endpoint. This is the default for direct ARM callers omitting `credentialMode`.
   - Both modes require the following credentials and a lease key:
   - A [fine-grained GitHub token](https://github.com/settings/personal-access-tokens/new) scoped to the configured repository with Issues and Contents read permissions. Do not start with repo write or Actions administration permissions.
   - An approved OpenAI API key issued by the operator's [OpenAI Platform project](https://platform.openai.com/api-keys). Use a dedicated budgeted project. The wizard stores the value; it does not mint either provider's credential.
   - The Entra web application client secret.
   - A dedicated managed lease signing key shared with your authorised signing controller. For a new deployment the wizard generates it by default; to attach to an existing controller, supply its Key Vault secret URI. Do not reuse the GitHub or Codex secret.
6. The wizard pre-fills the official, digest-pinned Linux/amd64 Caddy 2 and OAuth2 Proxy v7.15.6 images. The release ZIP also pre-fills the exact Symphony image published by CD to GHCR. A ZIP built locally or in PR CI does not guess a Symphony digest; supply your reviewed image reference or build the ZIP with `--symphony-image ghcr.io/owner/repository@sha256:<digest>`. All three fields are editable and require digest references. This template does not log in to private registries.

## Release image and Marketplace ZIP

Publishing a GitHub release runs Linux and Windows validation, builds the Linux/amd64 Symphony image with Git, GitHub CLI (`gh`), Node.js, npm and the pinned Codex CLI, smoke-tests it as the unprivileged container user without network access, then pushes it to `ghcr.io/releasedgroup/symphony-marketplace:<release-tag>`. The generated Marketplace ZIP defaults to the exact pushed digest only after the registry accepts an anonymous read and the same image passes its smoke test. The release carries the ZIP, its checksum, and `symphony-container-image.txt` containing the pinned reference.

Before the first release, check the organization’s package inventory for a name collision. After the first upload, verify the dedicated package’s owner, source repository, revision and complete version history against the approved release. Review the image contents for secrets before making only the dedicated Marketplace package public. Use the release job’s built-in token with package write access, scoped to the Symphony repository. Keep the existing private Symphony package and organization policies unchanged. If the anonymous check stopped the first run, compare the package digest to the successful publish job summary, then rerun only the failed jobs on the same tag. The verification job pulls the recorded digest and passes it to the ZIP builder. A push to a branch or PR builds and smoke-tests the image on CI but does not push it to a registry. This workflow does not update or publish the Marketplace listing.

## Managed lease signing key

- **Generate a new key** (default when creating a dedicated identity and vault): Azure runs a deployment script that generates 32 cryptographically random bytes, encodes them as 64 hexadecimal characters and writes them directly to a dedicated lease-only Key Vault. The form does not collect the value, and the deployment exposes only the `generatedLeaseSecretUri` output. No key value is printed. The separate script identity can manage secrets only in this lease vault; the VM identity only reads. This route also needs Azure Deployment Scripts (Azure Container Instances and temporary storage), permission to create their support resources, and connectivity to Key Vault.
- **Use existing Key Vault secret:** enter the HTTPS secret URI; the VM identity must already have permission to read that specific secret. For a controller already signing leases, prefer the wizard's **Use existing identity and vault references** option with a preauthorised identity. If using a newly created VM identity with an external secret, separately grant it permission before bootstrap can succeed. This choice never writes to the existing lease vault. For direct ARM callers using the legacy `leaseSigningKey` secure parameter, keep the default `leaseKeyMode=supplied`.
- **Connect your signing controller:** configure the independently authorised controller to read the same secret before dispatching work (scope its read access to that lease secret). This wizard does not install or authorise the controller and never bypasses lease validation. The secret is preserved on repeat deployments; do not run concurrent initial deployments into the same resource group or use complete-mode deployments to switch an existing instance between credential modes.

## Entra application and operator group

**Reuse an application:** enter its Application (client) ID and the operator group's Object ID, not the app object ID or the group's name. Configure the exact Web callback, single-tenant sign-in, application group claims and required assignment as described above. Supply the client secret in the masked field or enter its vault URI, depending on credential mode. The wizard does not modify an existing application.

**Create an application:** the tenant admin can use the repo's Python 3 helper from an interactive Azure CLI login in the target tenant. It creates a single-tenant application and enterprise application, requires operator-group assignment, and generates a 90-day app secret via Microsoft Graph directly into an existing Key Vault. Pass an existing security group via `--group`, or one or more direct user object IDs with repeatable `--operator` to create a new group. The helper prints IDs, expiry and the public vault reference, never credentials. Use the printed values in **Use existing identity and vault references**. The helper is in the repository, not the Marketplace ZIP. It does not configure a separate client/API scope or grant runtime Graph permissions.

```sh
python3 deploy/azure-marketplace/entra_setup.py --create-new-app \
  --tenant <tenant-guid> --dns-label <wizard-dns-label> --region <azure-region> \
  --vault <existing-vault-name> --secret-name <new-dedicated-secret-name> \
  --group <operator-group-object-id>
```

Use an Azure CLI user with delegated app registration, group and enterprise app assignment permissions in that tenant and vault secret metadata/write access. For example, Graph delegated permissions depend on the action: application write, group read or write, and app-role assignment. Tenant policy or the CLI's consent grants may require an administrator to use the Entra portal instead. This helper does not grant its own permissions. Inspect the emitted object IDs before retrying a partial run; do not blindly rerun it. Schedule rotation before the 90-day expiry. To use **Create dedicated identity and vault**, create the Entra application and secret in the Entra portal first (or use an existing app) and enter the credential in the masked field. The standard Marketplace wizard does not run administrator Graph operations.

## Codex CLI configuration and authentication

The digest-pinned Symphony image includes Codex CLI. The VM provisions a read-only `/home/symphony/.codex/config.toml` and sets `CODEX_HOME` explicitly. A distinct Responses API provider reads `OPENAI_API_KEY` directly in app-server, without relying on a saved Codex login. It keeps workspace-write with shell network access off and filters deployment secrets out of Codex's child shell environment. Symphony's lease and issue-label checks are unchanged.

In the Azure wizard, enter either a project API key in the masked vault-provisioning field or its HTTPS Key Vault **secret URI** in **Codex API key secret URL**. Use an approved OpenAI Platform project key, not a personal ChatGPT login. Startup checks the CLI and configuration offline without credentials, then the VM identity reads the secret and supplies it to the engine as `OPENAI_API_KEY`. It does not write the key to `config.toml` or save a Codex login. Host root and Docker administrators are trusted; Docker's container metadata can expose environment variables to those administrators. Do not share raw container inspection output. Shell filtering is not a new isolation boundary.

The offline preflight proves the CLI is present and can load the configuration; it does not validate the key, quota or model access. Verify those only with an explicitly approved task in the disposable lab. An absent, unreadable or malformed Key Vault secret fails startup. For rotation, point at a new version using the approved maintenance plan, or rotate the value of an unversioned reference and perform a controlled restart.

## Configure and package

From the repository root:

```sh
cd deploy/azure-marketplace
python3 -m unittest -v
python3 package.py --out ../../../symphony-marketplace-build
```

The ZIP has exactly `mainTemplate.json` and `createUiDefinition.json` at its root. Linux CI produces it in the `symphony-azure-marketplace` workflow artifact. The release CD workflow rebuilds and tests it, then attaches `symphony-marketplace.zip` and its SHA-256 checksum to published GitHub releases alongside the existing runtime packages. This does not upload or publish an Azure Marketplace offer.

In **Hosting**, choose **Generate new key pair** to have Azure create the recovery SSH key as part of **Review + create**. Download the private key when prompted and store it in your approved recovery location. The operator can also use an existing Azure key or paste an existing OpenSSH public key. The Symphony VM receives only the public key; the template does not open port 22 or enable password login. For non-portal deployment, supply an OpenSSH public key through `sshPublicKey` or an existing Azure SSH public key resource ID through `recoverySshKeyResourceId`. The deploying principal needs read access to a referenced public key resource and permission to create and generate one when using the new-key option.

Run Microsoft's [ARM template test toolkit](https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/test-toolkit) and the [Create UI Definition sandbox](https://learn.microsoft.com/en-us/azure/azure-resource-manager/managed-applications/test-createuidefinition). Validate the template with `az deployment group validate` and test a disposable customer-tenant deployment before submitting the ZIP under the Partner Center [Azure Application solution template plan](https://learn.microsoft.com/en-us/partner-center/marketplace-offers/plan-azure-app-solution-template). Test the generated-key download, both existing-key choices, both credential modes, least-privilege vault role and the administrator helper in that preview/lab. Offline tests cannot verify portal handoff or Graph consent. Run Command waits for engine health and an anonymous HTTPS 401 through the gateway. A failed startup fails provisioning; check the command's instance view and VM systemd logs.

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
