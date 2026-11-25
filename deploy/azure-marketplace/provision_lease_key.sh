#!/usr/bin/env bash
# Executed by Azure Deployment Scripts using a dedicated, lease-vault-only identity.
set +x
set -euo pipefail
umask 077
vault="${SYMPHONY_LEASE_VAULT:?}"
[[ "$vault" =~ ^[a-z0-9]{3,24}$ ]] || exit 2
name=lease-signing-key

# List metadata, not values. Allow for Azure RBAC propagation; never treat a
# failed list as an empty vault. Do not replace a disabled or existing secret.
for attempt in {1..30}; do
    if found=$(az keyvault secret list --vault-name "$vault" \
        --query "[?ends_with(id, '/$name')].id | [0]" --output tsv --only-show-errors 2>/dev/null); then
        break
    fi
    if (( attempt == 30 )); then
        echo 'Unable to list managed lease secret metadata in Key Vault.' >&2
        exit 3
    fi
    sleep 20
done
if [[ -n "$found" ]]; then
    [[ "$found" == "https://$vault.vault.azure.net/secrets/$name" ]] || exit 4
    exit 0
fi

file=$(mktemp)
trap 'rm -f -- "$file"' EXIT
openssl rand -hex 32 | tr -d '\n' > "$file"
if ! az keyvault secret set --vault-name "$vault" --name "$name" --file "$file" \
    --output none --only-show-errors >/dev/null 2>&1; then
    echo 'Unable to save the generated managed lease key in Key Vault.' >&2
    exit 5
fi
