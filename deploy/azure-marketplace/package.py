#!/usr/bin/env python3
"""Generate the Marketplace solution template and its matching Azure portal UI."""
import argparse
import base64
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parent
NETWORK = '2024-05-01'
COMPUTE = '2024-11-01'
IDENTITY = '2023-01-31'
VAULT = '2024-11-01'
IDENTITY_TYPE = 'Microsoft.ManagedIdentity/userAssignedIdentities'
VAULT_TYPE = 'Microsoft.KeyVault/vaults'
VAULT_ACCESS_ROLE = '4633458b-17de-408a-b874-0445c86b69e6'
SECRET_INPUTS = {
    'githubSecret': ('githubToken', 'github-token', 'GitHub fine-grained token'),
    'codexSecret': ('codexApiKey', 'codex-api-key', 'OpenAI project API key'),
    'oidcSecret': ('entraClientSecret', 'entra-client-secret', 'Entra application client secret'),
    'leaseSecret': ('leaseSigningKey', 'lease-signing-key', 'Managed lease signing key'),
}
SPECS = {
    'dnsLabel': ('DNS label', 'Choose a unique label for the public Azure DNS name; this is used in the Entra callback.', None, r'^[a-z][a-z0-9-]{1,61}[a-z0-9]$', 'hosting'),
    'sshPublicKey': ('Recovery SSH public key', 'Generate a key in Azure or supply an existing public key. This plan opens no SSH port.', None, None, 'hosting'),
    'identityResourceId': ('User-assigned identity resource ID', 'Existing identity with access to the four Key Vault secrets. Do not grant subscription-wide roles.', None, r'^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft.ManagedIdentity/userAssignedIdentities/[^/]+$', 'identity'),
    'entraClientId': ('Entra application client ID', 'Single-tenant app with assignment required and the Web callback registered. See setup guide.', None, r'^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$', 'identity'),
    'entraOperatorGroupId': ('Entra operator group object ID', 'Only principals with this immutable group ID in their signed Entra token can use the portal or API.', None, r'^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$', 'identity'),
    'githubSecret': ('GitHub token secret URL', 'HTTPS Key Vault secret URI. Enter the reference, not the token.', None, None, 'identity'),
    'codexSecret': ('Codex API key secret URL', 'HTTPS Key Vault secret URI. Enter the reference, not the key.', None, None, 'identity'),
    'oidcSecret': ('Entra client secret URL', 'HTTPS Key Vault secret URI. Enter the reference, not the secret.', None, None, 'identity'),
    'leaseSecret': ('Managed lease signing secret URL', 'Existing controller signing key, minimum 32 characters. Enter its Key Vault URI only.', None, None, 'identity'),
    'githubOwner': ('GitHub owner', 'GitHub organisation or account name.', None, r'^[A-Za-z0-9][A-Za-z0-9-]{0,38}$', 'workflow'),
    'githubRepo': ('GitHub repository', 'Repository name, without owner or URL. Deploy a separate instance per repository.', None, r'^[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$', 'workflow'),
    'baseBranch': ('Base branch', 'The existing branch from which Symphony creates worktrees.', 'main', r'^[A-Za-z0-9][A-Za-z0-9/_.-]{0,127}$', 'workflow'),
    'executionLabels': ('Execution labels', 'Comma-separated, no spaces around commas. Include symphony-approved; every label must be present.', 'symphony-approved', r'^[A-Za-z0-9][A-Za-z0-9_.: -]{0,49}(,[A-Za-z0-9][A-Za-z0-9_.: -]{0,49}){0,9}$', 'workflow'),
    'pollSeconds': ('Polling interval in seconds', '30 to 3600 seconds.', '600', r'^[1-9][0-9]{1,3}$', 'workflow'),
    'turnSeconds': ('Agent turn timeout in seconds', '60 to 7200 seconds.', '1800', r'^[1-9][0-9]{1,3}$', 'workflow'),
    'stallSeconds': ('Stall timeout in seconds', '30 to 3600 seconds and no greater than the turn timeout.', '300', r'^[1-9][0-9]{1,3}$', 'workflow'),
    'maxAgents': ('Maximum concurrent agents', 'One to five on this single-VM plan.', '1', r'^[1-5]$', 'workflow'),
    'symphonyImage': ('Symphony container image', 'Trusted, accessible Linux/amd64 registry image with @sha256 digest. Build from the reviewed fork.', None, None, 'images'),
    'caddyImage': ('Caddy container image', 'Trusted, accessible Linux/amd64 Caddy 2 image with @sha256 digest.', None, None, 'images'),
    'oauthImage': ('OAuth2 Proxy container image', 'Trusted, accessible Linux/amd64 image with @sha256 digest and the documented flags.', None, None, 'images'),
}
SECRET = r'^https://[a-z0-9-]{3,24}\.vault\.azure\.net/secrets/[A-Za-z0-9-]{1,127}(/[0-9a-fA-F]{32})?$'
IMAGE = r'^[a-z0-9]+([.:-][a-z0-9]+)+(/[a-z0-9]+([._-][a-z0-9]+)*)+@sha256:[a-f0-9]{64}$'
INTEGERS = {'pollSeconds': (30, 3600), 'turnSeconds': (60, 7200), 'stallSeconds': (30, 3600), 'maxAgents': (1, 5)}


def expr(src):
    return '[' + src + ']'


def p(name):
    return f"parameters('{name}')"


def v(name):
    return f"variables('{name}')"


def rid(typ, name):
    return f"resourceId('{typ}', {v(name)})"


def arm_string(text):
    return "'" + text.replace("'", "''") + "'"


def resources():
    loc = expr(p('location'))
    nsg_type, ip_type, vnet_type, nic_type = [
        'Microsoft.Network/' + kind for kind in ('networkSecurityGroups', 'publicIPAddresses', 'virtualNetworks', 'networkInterfaces')]
    vm_type = 'Microsoft.Compute/virtualMachines'

    def res(typ, api, name, props, deps=(), **kw):
        return dict(type=typ, apiVersion=api, name=expr(name), location=loc,
                    **({'dependsOn': [expr(dep) for dep in deps]} if deps else {}),
                    properties=props, **kw)

    def rule(name, port, priority, source='Internet', access='Allow'):
        return dict(name=name, properties=dict(protocol='Tcp' if access == 'Allow' else '*',
                    sourcePortRange='*', destinationPortRange=port, sourceAddressPrefix=source,
                    destinationAddressPrefix='*', access=access, priority=priority, direction='Inbound'))

    nsg = rid(nsg_type, 'nsg')
    ip = rid(ip_type, 'ip')
    net = rid(vnet_type, 'vnet')
    nic = rid(nic_type, 'nic')
    vm = rid(vm_type, 'vm')
    new = f"equals({p('credentialMode')}, 'provision')"
    identity = rid(IDENTITY_TYPE, 'managedIdentity')
    vault = rid(VAULT_TYPE, 'vault')
    resolved_identity = f"if({new}, {identity}, {p('identityResourceId')})"
    yield res(IDENTITY_TYPE, IDENTITY, v('managedIdentity'), {}, condition=expr(new))
    yield res(VAULT_TYPE, VAULT, v('vault'), dict(
        tenantId=expr('subscription().tenantId'), sku=dict(family='A', name='standard'), accessPolicies=[],
        enableRbacAuthorization=True, enableSoftDelete=True, enablePurgeProtection=True,
        enabledForTemplateDeployment=False, enabledForDeployment=False, enabledForDiskEncryption=False),
        condition=expr(new))
    role = f"guid({vault}, {identity}, '{VAULT_ACCESS_ROLE}')"
    yield dict(type='Microsoft.Authorization/roleAssignments', apiVersion='2022-04-01',
        name=expr(role), condition=expr(new), scope=expr(f"concat('{VAULT_TYPE}/', {v('vault')})"),
        dependsOn=[expr(vault), expr(identity)], properties=dict(
            roleDefinitionId=expr(f"subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '{VAULT_ACCESS_ROLE}')"),
            principalType='ServicePrincipal', principalId=expr(f"reference({identity}, '{IDENTITY}').principalId")))
    vault_ready = [f"extensionResourceId({vault}, 'Microsoft.Authorization/roleAssignments', {role})"]
    for value, name, _ in SECRET_INPUTS.values():
        typ = f'{VAULT_TYPE}/secrets'
        yield dict(type=typ, apiVersion=VAULT, condition=expr(new),
            name=expr(f"concat({v('vault')}, '/{name}')"), dependsOn=[expr(vault)],
            properties=dict(value=expr(p(value))))
        vault_ready.append(f"resourceId('{typ}', {v('vault')}, '{name}')")
    # Azure generates the key after Review + create. If the portal supplies
    # only the new resource ID, read its public property (never generate in ARM).
    supplied = p('sshPublicKey')
    from_resource = f"reference({p('recoverySshKeyResourceId')}, '2024-03-01').publicKey"
    key = f"if(and(empty(trim({supplied})), not(empty({p('recoverySshKeyResourceId')}))), {from_resource}, trim({supplied}))"
    # The credentials control documents a bare base64 RSA value as well as
    # accepting complete OpenSSH public keys. Prefix only recognised blobs.
    key_data = expr(f"if(startsWith({key}, 'AAAAB3NzaC1yc2E'), concat('ssh-rsa ', {key}), "
                    f"if(startsWith({key}, 'AAAAC3NzaC1lZDI1NTE5'), concat('ssh-ed25519 ', {key}), {key}))")
    yield res(nsg_type, NETWORK, v('nsg'), dict(securityRules=[
        rule('http-for-acme-and-redirect', '80', 100),
        rule('https-entra-gateway', '443', 110),
        rule('block-all-other-ingress', '*', 120, '*', 'Deny')]))
    yield res(ip_type, NETWORK, v('ip'), dict(publicIPAllocationMethod='Static', publicIPAddressVersion='IPv4',
              dnsSettings=dict(domainNameLabel=expr(p('dnsLabel')))), sku=dict(name='Standard'))
    yield res(vnet_type, NETWORK, v('vnet'), dict(addressSpace=dict(addressPrefixes=['10.62.0.0/24']),
              subnets=[dict(name='app', properties=dict(addressPrefix='10.62.0.0/26'))]))
    yield res(nic_type, NETWORK, v('nic'), dict(
        networkSecurityGroup=dict(id=expr(nsg)), ipConfigurations=[dict(name='app', properties=dict(
            privateIPAllocationMethod='Dynamic', publicIPAddress=dict(id=expr(ip)), subnet=dict(
                id=expr(f"resourceId('{vnet_type}/subnets', {v('vnet')}, 'app')"))))]), (nsg, ip, net))
    yield res(vm_type, COMPUTE, v('vm'), dict(
        hardwareProfile=dict(vmSize='Standard_D4s_v5'),
        osProfile=dict(computerName=expr(v('vm')), adminUsername='symphonyops',
            linuxConfiguration=dict(disablePasswordAuthentication=True,
                ssh=dict(publicKeys=[dict(path='/home/symphonyops/.ssh/authorized_keys', keyData=key_data)]))),
        storageProfile=dict(
            imageReference=dict(publisher='Canonical', offer='ubuntu-24_04-lts', sku='server', version='latest'),
            osDisk=dict(createOption='FromImage', diskSizeGB=128, deleteOption='Detach',
                        managedDisk=dict(storageAccountType='Premium_LRS'))),
        networkProfile=dict(networkInterfaces=[dict(id=expr(nic), properties=dict(primary=True))]),
        securityProfile=dict(securityType='TrustedLaunch', uefiSettings=dict(secureBootEnabled=True, vTpmEnabled=True))),
        (nic, identity), identity={'type': 'UserAssigned', 'userAssignedIdentities': expr(f"createObject({resolved_identity}, createObject())")})
    config = {
        'fqdn': v('fqdn'), 'tenantId': 'subscription().tenantId',
        'identityClientId': f"reference({resolved_identity}, '{IDENTITY}').clientId",
        'labels': f"split({p('executionLabels')}, ',')",
        **{x: p(x) for x in SPECS if x not in {'dnsLabel', 'sshPublicKey', 'identityResourceId', 'executionLabels', *SECRET_INPUTS}},
        **{key: f"if({new}, concat('https://', {v('vault')}, '.vault.azure.net/secrets/{name}'), {p(key)})"
           for key, (_, name, _) in SECRET_INPUTS.items()},
    }
    kv = ', '.join(f"'{key}', {value}" for key,value in config.items())
    prefix = base64.b64encode(b"#!/bin/bash\nexport SYMPHONY_CONFIG_B64='").decode()
    suffix = base64.b64encode(b"'\n").decode()
    inline = expr(f"concat(base64ToString('{prefix}'), base64(string(createObject({kv}))), "
                  f"base64ToString('{suffix}'), base64ToString({v('bootstrap')}))")
    yield res(f'{vm_type}/runCommands', COMPUTE, f"concat({v('vm')}, '/configure')", dict(
        source=dict(script=inline), asyncExecution=False, timeoutInSeconds=1800,
        treatFailureAsDeploymentFailure=True), (vm, *vault_ready))


def main_template():
    params = dict(location=dict(type='string', defaultValue=expr('resourceGroup().location'),
                               metadata=dict(description='Azure public-cloud deployment region.')))
    for key, (label, tip, value, _, _) in SPECS.items():
        param = dict(type='int' if key in INTEGERS else 'string', metadata=dict(description=tip))
        if key in INTEGERS:
            param['minValue'], param['maxValue'] = INTEGERS[key]
        if value is not None:
            param['defaultValue'] = int(value) if key in INTEGERS else value
        params[key] = param
    params['sshPublicKey']['defaultValue'] = ''
    params['recoverySshKeyResourceId'] = dict(type='string', defaultValue='',
        metadata=dict(description='Azure SSH public key resource ID. Used only if no public key text is supplied.'))
    params['credentialMode'] = dict(type='string', defaultValue='existing', allowedValues=['provision', 'existing'])
    for key, (value, _, label) in SECRET_INPUTS.items():
        params[key]['defaultValue'] = ''
        params[value] = dict(type='secureString', defaultValue='', metadata=dict(description=label))
    params['identityResourceId']['defaultValue'] = ''
    n = lambda suffix: expr(f"concat('sym-', uniqueString(resourceGroup().id), '-{suffix}')")
    variables = {name: n(name) for name in ['nsg','ip','vnet','nic','vm']}
    variables.update(managedIdentity=n('identity'), vault=expr("concat('sym', uniqueString(resourceGroup().id))"))
    # Use the deterministic name to register the callback before install.
    variables['fqdn'] = expr(f"concat({p('dnsLabel')}, '.', {p('location')}, '.cloudapp.azure.com')")
    variables['bootstrap'] = base64.b64encode((ROOT/'bootstrap.sh').read_bytes()).decode()
    base = f"concat('https://', {v('fqdn')}"
    outputs = {}
    for name, path in [('portalUrl','/'),('apiBaseUrl','/api/v1'),('healthUrl','/api/v1/health'),
                       ('entraRedirectUri','/oauth2/callback')]:
        outputs[name] = dict(type='string', value=expr(f"{base}, '{path}')"))
    outputs['vmName'] = dict(type='string', value=expr(v('vm')))
    return {'$schema': 'https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#',
            'contentVersion':'1.0.0.0','parameters':params,'variables':variables,
            'resources':list(resources()),'outputs':outputs}


def ui():
    steps = {
        'hosting': ('Hosting', 'One Ubuntu VM with a durable managed OS disk and HTTPS ingress.'),
        'identity': ('Entra and Key Vault', 'Choose a new dedicated managed identity and vault, or reuse existing references.'),
        'workflow': ('GitHub and runtime', 'Choose a repository, branch, labels and execution limits. Jobs require a current signed lease.'),
        'images': ('Pinned images', 'Specify trusted, digest-pinned containers. No mutable latest tags.'),
    }
    built = {step: dict(name=step,label=label,subLabel=dict(preValidation=label,postValidation=label),
                       bladeTitle=label, elements=[]) for step,(label, _) in steps.items()}
    outputs = {'location': '[location()]'}
    id_step = built['identity']['elements']
    id_step.append(dict(name='entraSetupHelp', type='Microsoft.Common.InfoBox', options=dict(icon='Info',
        text='Use an existing single-tenant app and operator group, or first run the tenant-admin Entra setup helper. The wizard does not grant directory permissions.',
        uri='https://github.com/ReleasedGroup/Symphony/tree/main/deploy/azure-marketplace#entra-application-and-operator-group')))
    mode = "steps('identity').credentialMode"
    new = f"equals({mode}, 'provision')"
    existing = f"equals({mode}, 'existing')"
    id_step.append(dict(name='credentialMode', type='Microsoft.Common.DropDown',
        label='Managed identity and Key Vault', defaultValue='Create dedicated identity and vault',
        constraints=dict(required=True, allowedValues=[
            dict(label='Create dedicated identity and vault', value='provision'),
            dict(label='Use existing identity and vault references', value='existing')])))
    outputs['credentialMode'] = expr(mode)
    for key, (label, tip, value, pattern, step) in SPECS.items():
        if key == 'sshPublicKey':
            name = 'recoverySsh'
            built[step]['elements'].append(dict(name=name, type='Microsoft.Compute.CredentialsCombo',
                label=dict(authenticationType='Recovery authentication', sshPublicKey=label),
                toolTip=dict(authenticationType='SSH public key only.', sshPublicKey=tip),
                constraints=dict(required=True), options=dict(hidePassword=True), osPlatform='Linux'))
            call = f"steps('{step}').{name}"
            outputs[key] = expr(f"coalesce({call}.sshPublicKey, '')")
            # An existing key selected in another resource group is passed as
            # public key text. A new key is created in this wizard's group.
            outputs['recoverySshKeyResourceId'] = expr(
                f"if({call}.generateNewSshKey, concat(subscription().id, '/resourceGroups/', "
                f"resourceGroup().name, '/providers/Microsoft.Compute/sshPublicKeys/', {call}.sshKeyName), '')")
            continue
        pattern = pattern or (IMAGE if key.endswith('Image') else SECRET)
        text = dict(name=key,type='Microsoft.Common.TextBox',label=label,toolTip=tip,
                    constraints=dict(required=True,regex=pattern,validationMessage=f'Enter a valid {label.lower()}.'))
        if value is not None:
            text['defaultValue'] = value
        built[step]['elements'].append(text)
        call = f"steps('{step}').{key}"
        outputs[key] = expr(f'int({call})' if key in INTEGERS else call)
        if key == 'identityResourceId' or key in SECRET_INPUTS:
            text['visible'] = expr(existing)
            text['constraints']['required'] = expr(existing)
            outputs[key] = expr(f"if({existing}, {call}, '')")
    for key, (value, _, label) in SECRET_INPUTS.items():
        length = 32 if key == 'leaseSecret' else 1
        id_step.append(dict(name=value, type='Microsoft.Common.PasswordBox',
            label=dict(password=label, confirmPassword=f'Confirm {label}'),
            toolTip='Enter a credential issued for this deployment. Azure stores it in the dedicated vault.',
            constraints=dict(required=expr(new), regex=r'^[A-Za-z0-9._~+/=:-]{' + str(length) + r',}$',
                             validationMessage=f'Enter a valid {label.lower()}.'),
            options=dict(hideConfirmation=True), visible=expr(new)))
        outputs[value] = expr(f"if({new}, steps('identity').{value}, '')")
    return {'$schema':'https://schema.management.azure.com/schemas/0.1.2-preview/CreateUIDefinition.MultiVm.json#',
            'handler':'Microsoft.Azure.CreateUIDef','version':'0.1.2-preview',
            'parameters':dict(config=dict(isWizard=True,basics=dict(description='Deploy a single-tenant Symphony instance for one GitHub repository.')),
                              basics=[],steps=list(built.values()),outputs=outputs)}


def files():
    return {name: (json.dumps(data,ensure_ascii=True,indent=2)+'\n').encode('utf-8') for name,data in
            [('mainTemplate.json',main_template()),('createUiDefinition.json',ui())]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out',type=Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True,exist_ok=True)
    with zipfile.ZipFile(args.out/'symphony-marketplace.zip','w',compression=zipfile.ZIP_DEFLATED) as archive:
        for name, payload in files().items():
            (args.out/name).write_bytes(payload)
            entry = zipfile.ZipInfo(name,date_time=(2020,1,1,0,0,0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(entry,payload)


if __name__ == '__main__':
    main()
