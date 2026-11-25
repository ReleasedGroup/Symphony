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
                         'healthUrl', 'entraRedirectUri', 'vmName', 'generatedLeaseSecretUri'})
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

    def test_wizard_deploys_with_azure_recovery_public_key(self):
        wizard = self.ui['parameters']
        hosting = next(step for step in wizard['steps'] if step['name'] == 'hosting')
        control = next(c for c in hosting['elements'] if c['name'] == 'recoverySsh')
        self.assertEqual(control['type'], 'Microsoft.Compute.CredentialsCombo')
        self.assertEqual(control['osPlatform'], 'Linux')
        self.assertTrue(control['options']['hidePassword'])
        self.assertTrue(control['constraints']['required'])
        public = wizard['outputs']['sshPublicKey']
        resource = wizard['outputs']['recoverySshKeyResourceId']
        self.assertEqual(public, "[coalesce(steps('hosting').recoverySsh.sshPublicKey, '')]")
        self.assertIn('recoverySsh.generateNewSshKey', resource)
        self.assertIn('/providers/Microsoft.Compute/sshPublicKeys/', resource)
        self.assertIn('resourceGroup().name', resource)
        self.assertIn('recoverySsh.sshKeyName', resource)
        self.assertNotIn('privateKey', json.dumps(wizard['outputs']))
        vm = next(r for r in self.template['resources'] if r['type'] == 'Microsoft.Compute/virtualMachines')
        keys = vm['properties']['osProfile']['linuxConfiguration']['ssh']['publicKeys']
        self.assertEqual(keys[0]['path'], '/home/symphonyops/.ssh/authorized_keys')
        expression = keys[0]['keyData']
        self.assertIn("empty(trim(parameters('sshPublicKey')))", expression)
        self.assertIn("reference(parameters('recoverySshKeyResourceId'), '2024-03-01').publicKey", expression)
        self.assertIn("concat('ssh-rsa '", expression)
        self.assertIn("concat('ssh-ed25519 '", expression)
        self.assertEqual(self.template['parameters']['recoverySshKeyResourceId']['defaultValue'], '')
        self.assertNotIn('generateKeyPair', json.dumps(self.template))

    def test_no_secrets_in_ui_and_zip_is_reproducible(self):
        for key in ['githubSecret','codexSecret','oidcSecret','leaseSecret']:
            field = self.template['parameters'][key]
            self.assertEqual(field.get('defaultValue'),'')
            self.assertIn('URL',field['metadata']['description'].replace('URI','URL'))
        with tempfile.TemporaryDirectory() as d:
            a,b = [Path(d)/x for x in ['a','b']]
            for path in [a,b]:
                subprocess.run([sys.executable,package.ROOT/'package.py','--out',path],check=True)
            self.assertEqual((a/'symphony-marketplace.zip').read_bytes(),(b/'symphony-marketplace.zip').read_bytes())
            with zipfile.ZipFile(a/'symphony-marketplace.zip') as archive:
                self.assertEqual(set(archive.namelist()), {'mainTemplate.json','createUiDefinition.json'})

    def test_release_uses_dedicated_marketplace_package(self):
        workflow = package.ROOT.parents[1] / '.github/workflows/release-packages.yml'
        assignments = [line.strip() for line in workflow.read_text().splitlines()
                       if line.strip().startswith('repository=')]
        self.assertEqual(assignments, ['repository="ghcr.io/${GITHUB_REPOSITORY,,}-marketplace"'])

    def test_image_defaults_for_local_and_release_zips(self):
        release_image = 'ghcr.io/releasedgroup/symphony-marketplace@sha256:' + 'f' * 64
        for configured in (None, release_image):
            with self.subTest(release_image=bool(configured)), tempfile.TemporaryDirectory() as d:
                args = ['--symphony-image', configured] if configured else []
                subprocess.run([sys.executable, package.ROOT / 'package.py', '--out', d, *args], check=True)
                with zipfile.ZipFile(Path(d) / 'symphony-marketplace.zip') as archive:
                    template = json.loads(archive.read('mainTemplate.json'))
                    ui = json.loads(archive.read('createUiDefinition.json'))
                fields = {c['name']: c for step in ui['parameters']['steps'] for c in step['elements']}
                for name, expected in {'symphonyImage': configured, 'caddyImage': package.CADDY_IMAGE,
                                       'oauthImage': package.OAUTH_IMAGE}.items():
                    self.assertEqual(fields[name].get('defaultValue'), expected)
                    self.assertEqual(template['parameters'][name].get('defaultValue'), expected)
                    self.assertTrue(fields[name]['constraints']['required'])
                    if expected is not None:
                        self.assertRegex(expected, package.IMAGE)
        for invalid in ('ghcr.io/releasedgroup/symphony-marketplace:latest', 'ghcr.io/releasedgroup/symphony-marketplace@sha256:123'):
            with self.assertRaises(ValueError):
                package.files(invalid)

    def test_provisioning_secrets_flow_only_from_secure_inputs_to_vault(self):
        resources = self.template['resources']
        step = next(s for s in self.ui['parameters']['steps'] if s['name'] == 'identity')
        fields = {c['name']:c for c in step['elements']}
        outputs = self.ui['parameters']['outputs']
        run = resources[-1]
        script = run['properties']['source']['script']
        for key, (value, secret, _) in package.SECRET_INPUTS.items():
            self.assertEqual(self.template['parameters'][value]['type'],'secureString')
            if key == 'leaseSecret':
                self.assertNotIn(value, fields)
                self.assertEqual(outputs[value], '')
            else:
                self.assertEqual(fields[value]['type'],'Microsoft.Common.PasswordBox')
                self.assertIn("'provision'",fields[value]['visible'])
            self.assertIn("'existing'",fields[key]['visible'])
            if key != 'leaseSecret':
                self.assertIn("'provision'",outputs[value])
            target = next(r for r in resources if r['type'].endswith('/vaults/secrets')
                          and secret in r['name'])
            self.assertEqual(target['properties']['value'], f"[parameters('{value}')]")
            self.assertIn("'provision'",target['condition'])
            self.assertNotIn(f"parameters('{value}')", script)
            self.assertNotIn(value, json.dumps(self.template['outputs']))
            self.assertIn(f'.vault.azure.net/secrets/{secret}',script)
            self.assertIn(f"parameters('{key}')",script)
            self.assertTrue(any(secret in dep for dep in run['dependsOn']))

    def test_generated_lease_scopes_and_exact_wizard_routes(self):
        wizard = self.ui['parameters']
        fields = {e['name']: e for step in wizard['steps'] for e in step['elements']}
        self.assertEqual(fields['leaseKeyMode']['defaultValue'], 'Generate a new key')
        self.assertEqual(wizard['outputs']['leaseKeyMode'],
                         "[if(equals(steps('identity').credentialMode, 'existing'), 'existing', steps('identity').leaseKeyMode)]")
        self.assertEqual(fields['leaseSecret']['visible'], fields['leaseSecret']['constraints']['required'])
        self.assertIn("steps('identity').leaseKeyMode, 'existing'", fields['leaseSecret']['visible'])
        resources = self.template['resources']
        generator = next(r for r in resources if r['type'] == package.SCRIPT_TYPE)
        condition = "[and(equals(parameters('credentialMode'), 'provision'), equals(parameters('leaseKeyMode'), 'generate'))]"
        self.assertEqual(generator['condition'], condition)
        self.assertEqual(generator['properties']['cleanupPreference'], 'Always')
        self.assertNotIn('outputs', generator['properties'])
        self.assertIn('leaseWriter', json.dumps(generator['identity']))
        self.assertNotIn('leaseWriter', json.dumps(next(r for r in resources if r['type'] == 'Microsoft.Compute/virtualMachines')))
        roles = [r for r in resources if r['type'] == 'Microsoft.Authorization/roleAssignments' and r['condition'] == condition]
        self.assertEqual(len(roles), 2)
        for r in roles:
            self.assertIn("variables('leaseVault')", r['scope'])
            self.assertIn('null()', r['properties']['principalId'])
        self.assertIn(package.LEASE_WRITER_ROLE, roles[0]['properties']['roleDefinitionId'])
        self.assertIn(package.VAULT_ACCESS_ROLE, roles[1]['properties']['roleDefinitionId'])
        self.assertIn(package.SCRIPT_TYPE, json.dumps(resources[-1]['dependsOn']))
        self.assertEqual(self.template['outputs']['generatedLeaseSecretUri']['condition'], condition)
        self.assertIn("variables('leaseVault')", self.template['outputs']['generatedLeaseSecretUri']['value'])
        self.assertEqual(self.template['parameters']['leaseKeyMode']['defaultValue'], 'supplied')

    def test_lease_generator_executes_and_preserves_an_existing_secret(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d)
            secret = path / 'simulated-vault'
            (path / 'az').write_text("""#!/bin/bash
if [[ "$3" == list ]]; then
    [[ "${FAIL_LIST:-}" != 1 ]] || exit 1
    if [[ -f "$SIMULATED_VAULT" ]]; then
        echo 'https://symleaseexample.vault.azure.net/secrets/lease-signing-key'
    fi
    exit 0
fi
[[ "$3" == set ]] || exit 8
while [[ "$1" != --file && $# -gt 1 ]]; do shift; done
[[ "$1" == --file && "$3" == --output && "$4" == none ]] || exit 9
cp -- "$2" "$SIMULATED_VAULT"
""")
            (path / 'sleep').write_text('#!/bin/bash\nexit 0\n')
            for tool in ('az', 'sleep'):
                (path / tool).chmod(0o700)
            env = {**os.environ, 'PATH': f"{path}{os.pathsep}{os.environ['PATH']}",
                   'SYMPHONY_LEASE_VAULT': 'symleaseexample', 'SIMULATED_VAULT': str(secret)}
            script = next(r['properties']['scriptContent'] for r in self.template['resources']
                          if r['type'] == package.SCRIPT_TYPE)
            def run(environment=env):
                return subprocess.run(['bash', '-s'], input=script, text=True,
                                      capture_output=True, env=environment, check=False)

            first = run()
            self.assertEqual((first.returncode, first.stdout, first.stderr), (0, '', ''))
            key = secret.read_text()
            self.assertRegex(key, r'^[a-f0-9]{64}$')
            second = run()
            self.assertEqual((second.returncode, second.stdout, second.stderr), (0, '', ''))
            self.assertEqual(secret.read_text(), key)
            secret.unlink()
            failure = run({**env, 'FAIL_LIST': '1'})
            self.assertEqual(failure.returncode, 3)
            self.assertFalse(secret.exists())
            self.assertNotIn(key, failure.stdout + failure.stderr)

    def test_new_identity_and_vault_are_opt_in_and_the_grant_is_vault_scoped(self):
        resources = self.template['resources']
        vault = next(r for r in resources if r['type'] == package.VAULT_TYPE)
        identity = next(r for r in resources if r['type'] == package.IDENTITY_TYPE)
        role = next(r for r in resources if r['type'] == 'Microsoft.Authorization/roleAssignments')
        self.assertEqual(self.template['parameters']['credentialMode']['defaultValue'],'existing')
        self.assertTrue(vault['properties']['enableRbacAuthorization'])
        self.assertTrue(vault['properties']['enablePurgeProtection'])
        self.assertFalse(vault['properties']['enabledForTemplateDeployment'])
        self.assertEqual(role['scope'], "[concat('Microsoft.KeyVault/vaults/', variables('vault'))]")
        self.assertIn(package.VAULT_ACCESS_ROLE, role['properties']['roleDefinitionId'])
        self.assertEqual(role['properties']['principalId'],
            "[if(equals(parameters('credentialMode'), 'provision'), "
            "reference(resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', "
            "variables('managedIdentity')), '2023-01-31').principalId, null())]")
        for r in [vault, identity, role]:
            self.assertEqual(r['condition'], "[equals(parameters('credentialMode'), 'provision')]")
        self.assertIn('extensionResourceId',json.dumps(resources[-1]['dependsOn']))
        expected_identity = ("if(equals(parameters('credentialMode'), 'provision'), "
                             "resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', "
                             "variables('managedIdentity')), parameters('identityResourceId'))")
        vm = next(r for r in resources if r['type'] == 'Microsoft.Compute/virtualMachines')
        self.assertIn(expected_identity, vm['identity']['userAssignedIdentities'])
        self.assertIn(f"reference({expected_identity}, '2023-01-31').clientId",
                      resources[-1]['properties']['source']['script'])

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
        self.assertEqual(options['model_provider'], 'marketplace_openai')
        self.assertNotIn('forced_login_method', options)
        self.assertEqual(options['model_providers'][options['model_provider']], {
            'name': 'OpenAI API', 'base_url': 'https://api.openai.com/v1',
            'env_key': 'OPENAI_API_KEY', 'wire_api': 'responses',
            'requires_openai_auth': False, 'supports_websockets': False,
        })
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
