# Symphony Marketplace listing

## Name

Symphony for Azure

## Search results summary

Run approved GitHub coding work from an Entra-protected Azure instance.

## Short description

Deploy Symphony in your Azure subscription. Run Codex-assisted GitHub work for one configured repository, with Microsoft Entra ID access, Azure Key Vault credentials and managed lease controls.

## Description

Symphony for Azure is Released's deployment package for teams that want to run Codex-assisted software delivery in their own Azure subscription. The Community plan deploys an Azure Managed application in the customer's subscription. It provisions a dedicated Ubuntu host and starts Symphony with an HTTPS dashboard and API protected by Microsoft Entra ID.

Configure a GitHub repository, base branch, required issue labels and workload limits at deployment. Operators in your designated Entra group can review runtime status through the portal and use the protected API. Azure Key Vault provides access to the configured credentials through a managed identity.

Each instance targets one GitHub repository. To start work, an issue must match the configured labels and Symphony must hold a valid managed lease. A separately configured signing controller issues and renews leases.

Deployment requires an Azure subscription with the applicable resource permissions and quota, a single-tenant Entra application and operator group, and credentials for GitHub and an OpenAI API project. The workload calls GitHub and OpenAI services. Azure infrastructure and third-party API usage may incur separate charges.

Use the deployment guide to prepare your subscription, register the HTTPS callback and deploy into a dedicated resource group. Validate operator access and confirm idle behaviour before enabling work.

## Search keywords

- GitHub issue automation
- AI software delivery
- DevOps orchestration

## Logos

| Size | PNG |
| --- | --- |
| 216 × 216 | [Large logo](Symphony-Marketplace-Large-216x216.png) |
| 90 × 90 | [Medium logo](Symphony-Marketplace-Medium-90x90.png) |
| 48 × 48 | [Small logo](Symphony-Marketplace-Small-48x48.png) |

## Optional screenshot capture list

Capture genuine 1280 × 720 PNGs using synthetic fixtures. Frame each view to exclude account names, resource URLs, tenant and repository identifiers, and credentials.

| Screen | Suggested caption |
| --- | --- |
| Deployment wizard with example inputs | Configure Symphony in the Azure deployment wizard. |
| Signed-in dashboard for an idle instance | Review runtime status from the Entra-protected Symphony dashboard. |
| Work status and lease state | Check work status and lease state in the operator portal. |

## Certification notes

Use a disposable test resource group, a synthetic repository, and test identities representing an operator group member and a non-member.

1. Open the Community plan’s Managed application deployment wizard. Confirm required wizard fields are validated and the default image reference includes a SHA-256 digest.
2. Record the deployment result. For a successful deployment, use the generated portal and API addresses to verify HTTPS and the configured redirect URI.
3. Check the portal and API while signed out, as a test operator, and as a test non-member. Verify only the operator can read operational data.
4. Confirm the service starts idle without a managed lease. In an authorized test fixture, compare two synthetic issues, one with all required labels and one missing a label. Record lease-gated behaviour only for cases actually exercised.
5. Record provisioning, access and issue-selection results separately. Keep test identities, environment details and reviewer access instructions outside public materials.

[Azure deployment and operator guide](../README.md)
