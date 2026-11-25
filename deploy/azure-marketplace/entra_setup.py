#!/usr/bin/env python3
"""Tenant-admin preflight: create a single-tenant Symphony app and vault its credential.

Never invoked by the Marketplace deployment or the Symphony runtime identity.
Requires an interactive Azure CLI user in the intended tenant.
"""
import argparse
from datetime import datetime, timedelta, timezone
import json
import re
import subprocess
from urllib.error import HTTPError
from urllib.request import build_opener, HTTPRedirectHandler, Request
from uuid import UUID

GRAPH = 'https://graph.microsoft.com/v1.0'
VAULT_AUDIENCE = 'https://vault.azure.net'
DEFAULT_ROLE = '00000000-0000-0000-0000-000000000000'


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def http(method, url, token, payload=None):
    body = None if payload is None else json.dumps(payload).encode()
    req = Request(url, data=body, method=method, headers={
        'Authorization': f'Bearer {token}', 'Content-Type': 'application/json'})
    with build_opener(NoRedirect()).open(req, timeout=30) as res:
        data = res.read()
    return json.loads(data) if data else {}


def az_token(resource, tenant):
    result = subprocess.run(['az', 'account', 'get-access-token', '--tenant', tenant,
                             '--resource', resource, '--query', 'accessToken',
                             '--output', 'tsv', '--only-show-errors'],
                            check=True, capture_output=True, text=True)
    return result.stdout.strip()


def object_id(value):
    result = UUID(value)
    if result.int == 0:
        raise ValueError('zero object ID')
    return str(result)


def configure(args, graph_token, vault_token, transport=http):
    def graph(method, endpoint, data=None):
        return transport(method, GRAPH + endpoint, graph_token, data)

    secret_url = f'https://{args.vault}.vault.azure.net/secrets/{args.secret_name}'
    try:
        versions = transport('GET', secret_url + '/versions?maxresults=1&api-version=7.4', vault_token)
    except HTTPError as e:
        if e.code != 404:
            raise
    else:
        if versions.get('value'):
            raise ValueError('Refusing to overwrite an existing Key Vault secret. Choose a dedicated new secret name.')
    group = args.group
    if group:
        found = graph('GET', '/groups/' + group + '?$select=id,securityEnabled')
        if not found.get('securityEnabled') or found.get('id') != group:
            raise ValueError('The selected object is not a security group.')
    else:
        group = object_id(graph('POST', '/groups', {
            'displayName': f'Symphony operators ({args.dns_label})',
            'mailEnabled': False, 'mailNickname': f'symphony-{args.dns_label}'[:64],
            'securityEnabled': True,
            'members@odata.bind': [GRAPH + '/users/' + user for user in args.operator],
        })['id'])
        print(f'Created operator group: {group}', flush=True)

    callback = f'https://{args.dns_label}.{args.region}.cloudapp.azure.com/oauth2/callback'
    app = graph('POST', '/applications', {
        'displayName': f'Symphony ({args.dns_label})', 'signInAudience': 'AzureADMyOrg',
        'web': {'redirectUris': [callback]}, 'groupMembershipClaims': 'ApplicationGroup',
        'api': {'requestedAccessTokenVersion': 2},
    })
    app_id, app_object = object_id(app['appId']), object_id(app['id'])
    print(f'Created Entra application: {app_id} (object {app_object})', flush=True)
    principal = object_id(graph('POST', '/servicePrincipals', {
        'appId': app_id, 'appRoleAssignmentRequired': True,
    })['id'])
    graph('POST', f'/servicePrincipals/{principal}/appRoleAssignedTo', {
        'principalId': group, 'resourceId': principal, 'appRoleId': DEFAULT_ROLE,
    })
    expiry = (datetime.now(timezone.utc) + timedelta(days=90)).isoformat(timespec='seconds')
    password = graph('POST', f'/applications/{app_object}/addPassword', {
        'passwordCredential': {'displayName': 'Symphony initial bootstrap', 'endDateTime': expiry},
    })
    try:
        transport('PUT', secret_url + '?api-version=7.4', vault_token,
                  {'value': password['secretText'], 'attributes': {
                      'exp': int(datetime.fromisoformat(expiry).timestamp())}})
    except Exception:
        try:
            graph('POST', f'/applications/{app_object}/removePassword', {
                'keyId': object_id(password['keyId']),
            })
        except Exception:
            print('Credential rollback failed. Remove the new app credential in Entra before retrying.', flush=True)
        raise
    return {'entraClientId': app_id, 'entraOperatorGroupId': group,
            'oidcSecret': secret_url, 'entraRedirectUri': callback, 'credentialExpires': expiry}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--tenant', required=True, type=object_id)
    p.add_argument('--dns-label', required=True)
    p.add_argument('--region', required=True)
    p.add_argument('--vault', required=True)
    p.add_argument('--secret-name', required=True, help='Use a new secret name dedicated to this application.')
    g = p.add_mutually_exclusive_group(required=True)
    g.add_argument('--group', type=object_id, help='Existing operator security group object ID.')
    g.add_argument('--operator', action='append', type=object_id, help='Create a new group with these direct user object IDs.')
    p.add_argument('--create-new-app', action='store_true', required=True, help='Explicitly authorise creation of a new app, not a modification of an existing one.')
    a = p.parse_args()
    if not (re.fullmatch(r'[a-z][a-z0-9-]{1,61}[a-z0-9]', a.dns_label)
            and re.fullmatch(r'[a-z][a-z0-9]{1,31}', a.region)
            and re.fullmatch(r'[a-z][a-z0-9-]{1,22}[a-z0-9]', a.vault)
            and re.fullmatch(r'[a-zA-Z0-9-]{1,127}', a.secret_name)
            and (not a.operator or len(set(a.operator)) == len(a.operator) <= 20)):
        p.error('Invalid Azure name or operator list.')
    try:
        result = configure(a, az_token('https://graph.microsoft.com/', a.tenant),
                           az_token(VAULT_AUDIENCE, a.tenant))
    except HTTPError as e:
        p.exit(1, f'Entra/Key Vault setup stopped (HTTP {e.code}). Inspect resources shown above before retrying.\n')
    except Exception:
        p.exit(1, 'Entra/Key Vault setup stopped. Check tenant sign-in and permissions; inspect any resources shown above.\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
