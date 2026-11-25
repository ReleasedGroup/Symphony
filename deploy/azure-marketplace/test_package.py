"""Local contract tests; run with Python 3, bash, jq and git."""
import base64
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import tomllib
import unittest
import zipfile

import package


class MarketplacePackageTest(unittest.TestCase):
    def setUp(self):
        self.template = package.main_template()
        self.ui = package.ui()
        self.bootstrap = package.ROOT / 'bootstrap.sh'

    def config(self):
        image = 'ghcr.io/example/symphony@sha256:' + 'a' * 64
        return dict(fqdn='sym-example.australiaeast.cloudapp.azure.com',
                    tenantId='11111111-1111-1111-1111-111111111111',
                    entraClientId='22222222-2222-2222-2222-222222222222',
                    identityClientId='33333333-3333-3333-3333-333333333333',
                    entraOperatorGroupId='44444444-4444-4444-4444-444444444444',
                    githubOwner='ReleasedGroup', githubRepo='Symphony',
                    baseBranch='releases/v1.2', labels=['symphony-approved','Type: Delivery'],
                    pollSeconds=600, turnSeconds=1800, stallSeconds=300, maxAgents=1,
                    symphonyImage=image, oauthImage=image, caddyImage=image,
                    **{name+'Secret': f'https://vault-example.vault.azure.net/secrets/{name}'
                       for name in ['github','codex','oidc','lease']})

    def validate(self, overrides=None, remove=None):
        config = self.config()
        config.update(overrides or {})
        if remove:
            config.pop(remove)
        env = {**os.environ, 'SYMPHONY_CONFIG_B64':base64.b64encode(json.dumps(config).encode()).decode()}
        return subprocess.run(['bash', self.bootstrap, '--validate-only'], env=env,
                              text=True, capture_output=True, check=False)

    def test_outputs_and_wizard_inputs_have_exact_parity(self):
        self.assertEqual(set(self.template['parameters']), set(self.ui['parameters']['outputs']))
        for key in package.INTEGERS:
            self.assertIn('int(',self.ui['parameters']['outputs'][key])
        self.assertEqual(set(self.template['outputs']), {'portalUrl', 'apiBaseUrl',
                         'healthUrl', 'entraRedirectUri', 'vmName'})
        self.assertIn('/api/v1',self.template['outputs']['apiBaseUrl']['value'])
        self.assertIn('/oauth2/callback',self.template['outputs']['entraRedirectUri']['value'])

    def test_inbound_network_and_persistence_contract(self):
        resources = self.template['resources']
        nsg = next(r for r in resources if r['type'].endswith('/networkSecurityGroups'))
        rules = [r['properties'] for r in nsg['properties']['securityRules']]
        self.assertEqual({r['destinationPortRange'] for r in rules if r['access']=='Allow'}, {'80','443'})
        self.assertEqual(rules[-1]['access'],'Deny')
        vm = next(r for r in resources if r['type'].endswith('/virtualMachines'))
        self.assertTrue(vm['properties']['osProfile']['linuxConfiguration']['disablePasswordAuthentication'])
        self.assertEqual(vm['properties']['storageProfile']['osDisk']['deleteOption'],'Detach')
        self.assertEqual(vm['identity']['type'], 'UserAssigned')
        run = resources[-1]['properties']
        self.assertTrue(run['treatFailureAsDeploymentFailure'])
        self.assertFalse(run['asyncExecution'])
        self.assertEqual(self.bootstrap.read_bytes(),base64.b64decode(self.template['variables']['bootstrap']))

    def test_no_secrets_in_ui_and_zip_is_reproducible(self):
        for key in ['githubSecret','codexSecret','oidcSecret','leaseSecret']:
            field = self.template['parameters'][key]
            self.assertNotIn('defaultValue',field)
            self.assertIn('URL',field['metadata']['description'].replace('URI','URL'))
        with tempfile.TemporaryDirectory() as d:
            a,b = [Path(d)/x for x in ['a','b']]
            for path in [a,b]:
                subprocess.run([sys.executable,package.ROOT/'package.py','--out',path],check=True)
            self.assertEqual((a/'symphony-marketplace.zip').read_bytes(),(b/'symphony-marketplace.zip').read_bytes())
            with zipfile.ZipFile(a/'symphony-marketplace.zip') as archive:
                self.assertEqual(set(archive.namelist()), {'mainTemplate.json','createUiDefinition.json'})

    def test_validated_normal_config_and_limits(self):
        cases = [{},dict(pollSeconds=30,turnSeconds=60,stallSeconds=30,maxAgents=5),
                 dict(pollSeconds=3600,turnSeconds=7200,stallSeconds=3600,maxAgents=1)]
        for config in cases:
            result = self.validate(config)
            self.assertEqual(result.returncode,0,result.stderr)

    def test_invalid_config_fails_before_install(self):
        cases = [dict(labels=[]),dict(labels=['bug']),dict(labels=['symphony-approved']*2),
                 dict(labels=['symphony-approved','bad\nlabel']),dict(stallSeconds=1801),
                 dict(turnSeconds=7201),dict(maxAgents=6),dict(pollSeconds=29),dict(pollSeconds=30.5),
                 dict(githubOwner='github.com/other'),dict(githubRepo='x/other'),
                 dict(baseBranch='foo/../main'),dict(baseBranch='main@{1}'),dict(baseBranch='HEAD'),
                 dict(symphonyImage='ghcr.io/example/symphony:latest'),
                 dict(oidcSecret='https://evil.example/secrets/key'),
                 dict(leaseSecret=self.config()['oidcSecret']),
                 dict(fqdn='x.cloudapp.azure.com.evil.example'),dict(tenantId='common'),dict(entraOperatorGroupId='*')]
        for config in cases:
            with self.subTest(config=config):
                self.assertNotEqual(self.validate(config).returncode,0)
        self.assertNotEqual(self.validate(remove='githubSecret').returncode,0)

    def test_codex_preflight_and_secret_free_mount(self):
        s = self.bootstrap.read_text()
        config = s.split("<<'CODEX_CONFIG'\n", 1)[1].split('\nCODEX_CONFIG\n', 1)[0]
        options = tomllib.loads(config)
        self.assertEqual(options['model_provider'], 'openai')
        self.assertEqual(options['forced_login_method'], 'api')
        self.assertEqual(options['sandbox_mode'], 'workspace-write')
        self.assertEqual(options['approval_policy'], 'never')
        self.assertFalse(options['sandbox_workspace_write']['network_access'])
        self.assertIn('OPENAI_API_KEY', options['shell_environment_policy']['exclude'])
        self.assertNotIn('$codex', config)
        self.assertIn('target=$codex_home/config.toml,readonly', s)
        self.assertIn('CODEX_HOME=$codex_home', s)
        self.assertIn('OPENAI_API_KEY=$codex', s)
        preflight = s.split('if ! docker run --rm --network none', 1)[1].split('\nfi\n', 1)[0]
        self.assertIn('codex app-server --help', preflight)
        self.assertIn('codex features list', preflight)
        self.assertNotIn('engine.env', preflight)
        self.assertLess(s.index('codex features list'), s.index('codex=$(secret .codexSecret)'))
        self.assertLess(s.index('codex features list'), s.index('docker run -d --name symphony-mp-engine'))

    def test_runtime_keeps_gate_and_unpublished_engine_port(self):
        s = self.bootstrap.read_text()
        self.assertIn('ManagedLease__Enabled=true',s)
        self.assertIn('thread_sandbox: workspace-write',s)
        self.assertIn('turn_sandbox_policy: workspace-write',s)
        self.assertIn('---\nWork only on the approved software maintenance issue',s)
        self.assertIn('169.254.169.254/32 -j DROP',s)
        self.assertIn('== 401',s)
        self.assertIn('https://login.microsoftonline.com/$tenant/v2.0',s)
        self.assertIn('--skip-jwt-bearer-tokens=true',s)
        self.assertIn('--allowed-group="$operators" --oidc-groups-claim=groups',s)
        self.assertIn('--bearer-token-login-fallback=false',s)
        self.assertIn('respond @workflowWrite 403',s)
        self.assertNotIn('socket', ''.join(line for line in s.splitlines() if line.startswith('  -v')))
        self.assertNotIn('-p 8080:',s)
        subprocess.run(['bash','-n',self.bootstrap],check=True)
        inner = s.split("<<'RUN'\n",1)[1].split('\nRUN\n',1)[0]
        subprocess.run(['bash','-n'],input=inner,text=True,check=True)


if __name__ == '__main__':
    unittest.main()
