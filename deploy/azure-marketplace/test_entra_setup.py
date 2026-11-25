import argparse
from contextlib import redirect_stdout
import io
import unittest
from urllib.error import HTTPError

import entra_setup as setup

GROUP = '11111111-1111-1111-1111-111111111111'
APP = '22222222-2222-2222-2222-222222222222'
OBJECT = '33333333-3333-3333-3333-333333333333'
PRINCIPAL = '44444444-4444-4444-4444-444444444444'
KEY = '55555555-5555-5555-5555-555555555555'
FAKE = 'synthetic-test-credential'


class EntraSetupTest(unittest.TestCase):
    def run_flow(self, *, group=True, fails=False, occupied=False):
        a = argparse.Namespace(group=GROUP if group else None, operator=[OBJECT],
            dns_label='sym-test', region='australiaeast', vault='sym-test-vault',
            secret_name='entra-symphony')
        calls = []

        def transport(method, url, token, data=None):
            calls.append((method, url, token, data))
            if 'vault.azure.net' in url:
                self.assertEqual(token, 'fake-vault-token')
                status = (200 if occupied else 404) if method == 'GET' else (403 if fails else 200)
                if status != 200:
                    raise HTTPError(url, status, 'mock', {}, None)
                return {'value': [{'id': 'existing-version'}]} if method == 'GET' else {}
            self.assertEqual(token, 'fake-graph-token')
            if '/groups' in url:
                return {'id': GROUP, 'securityEnabled': True}
            if url.endswith('/applications'):
                return {'id': OBJECT, 'appId': APP}
            if url.endswith('/servicePrincipals'):
                return {'id': PRINCIPAL}
            if url.endswith('/addPassword'):
                return {'secretText': FAKE, 'keyId': KEY}
            return {}

        stdout = io.StringIO()
        with redirect_stdout(stdout):
            if fails or occupied:
                with self.assertRaises(ValueError if occupied else HTTPError):
                    setup.configure(a, 'fake-graph-token', 'fake-vault-token', transport)
                result = {}
            else:
                result = setup.configure(a, 'fake-graph-token', 'fake-vault-token', transport)
        self.assertNotIn(FAKE, stdout.getvalue())
        self.assertNotIn(FAKE, str(result))
        return calls, result

    def test_sets_assignment_required_and_only_vault_receives_secret(self):
        calls, result = self.run_flow(group=False)
        self.assertEqual(result['entraClientId'], APP)
        self.assertEqual(result['entraOperatorGroupId'], GROUP)
        self.assertEqual([url for _, url, _, data in calls if FAKE in str(data)],
                         [result['oidcSecret'] + '?api-version=7.4'])
        app = next(d for _, u, _, d in calls if u.endswith('/applications'))
        self.assertEqual(app['signInAudience'], 'AzureADMyOrg')
        self.assertEqual(app['groupMembershipClaims'], 'ApplicationGroup')
        self.assertEqual(app['web']['redirectUris'], [result['entraRedirectUri']])
        sp = next(d for _, u, _, d in calls if u.endswith('/servicePrincipals'))
        self.assertTrue(sp['appRoleAssignmentRequired'])
        grant = next(d for _, u, _, d in calls if u.endswith('/appRoleAssignedTo'))
        self.assertEqual(grant['principalId'], GROUP)

    def test_refuses_existing_vault_secret_before_mutation(self):
        calls, _ = self.run_flow(occupied=True)
        self.assertEqual([m for m, *_ in calls], ['GET'])

    def test_removes_generated_credential_if_vault_write_fails(self):
        calls, _ = self.run_flow(fails=True)
        self.assertTrue(calls[-1][1].endswith('/removePassword'))
        self.assertEqual(calls[-1][3], {'keyId': KEY})


if __name__ == '__main__':
    unittest.main()
