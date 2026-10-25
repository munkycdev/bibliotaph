<#
.SYNOPSIS
    Creates or updates everything bibliotaph.online runs on in Azure.

.DESCRIPTION
    Run it from the repository root, signed in with az login to the subscription that holds the Container Apps
    environment. Every step checks what's already there, so running it again only fills in what's missing and refreshes
    the app's settings.

    It makes, in its own resource group: a user-assigned identity the app runs as, a storage account (Litestream's
    replica, the Data Protection key ring and covers), a Key Vault (the key that wraps the key ring, and the sign-in secrets),
    the Microsoft sign-in app registration, the container app (one replica, never scaled), and the app registration
    GitHub Actions deploys with. It reuses an existing Container Apps environment and container registry.

.EXAMPLE
    ./infra/setup.ps1 -Registry nornisacr -Environment nornis-env -EnvironmentResourceGroup rg-nornis

.EXAMPLE
    # Once the DNS records it printed have propagated:
    ./infra/setup.ps1 -Registry nornisacr -Environment nornis-env -EnvironmentResourceGroup rg-nornis -BindDomain
#>
#Requires -Version 7.3
[CmdletBinding()]
param(
    # The existing container registry the image is pushed to and pulled from.
    [Parameter(Mandatory)] [string] $Registry,
    # The existing Container Apps environment, and its resource group.
    [Parameter(Mandatory)] [string] $Environment,
    [Parameter(Mandatory)] [string] $EnvironmentResourceGroup,

    [string] $ResourceGroup = 'rg-bibliotaph',
    [string] $StorageAccount = 'stbibliotaph',
    [string] $KeyVault = 'kv-bibliotaph',
    [string] $Identity = 'id-bibliotaph',
    [string] $App = 'bibliotaph-app',
    [string] $Domain = 'bibliotaph.online',
    [string] $GitHubRepository = 'munkycdev/bibliotaph',

    # From the Google Cloud console (APIs & Services, Credentials, OAuth client ID, Web application). Its secret is
    # asked for once and kept in Key Vault.
    [string] $GoogleClientId,

    # Adds the domain and www to the app and issues managed certificates. Needs the DNS records in place first.
    [switch] $BindDomain
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repoRoot = Split-Path $PSScriptRoot -Parent

function Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Json { $args[0] | ConvertFrom-Json }

# az prints "not found" errors for things that don't exist yet; those return $null here instead of stopping the script.
function Find {
    $PSNativeCommandUseErrorActionPreference = $false
    $result = az @args 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $result) { return $null }
    $result | ConvertFrom-Json
}

function Grant([string] $principalId, [string] $principalType, [string] $role, [string] $scope) {
    $existing = az role assignment list --assignee $principalId --role $role --scope $scope --query 'length(@)' -o tsv
    if ($existing -ne '0') { return }
    az role assignment create --assignee-object-id $principalId --assignee-principal-type $principalType `
        --role $role --scope $scope -o none
    Write-Host "  granted $role"
}

# Role assignments take a little while to reach Key Vault's data plane.
function Retry([scriptblock] $action) {
    for ($attempt = 1; ; $attempt++) {
        try { return & $action }
        catch {
            if ($attempt -ge 12) { throw }
            Write-Host "  waiting for access to apply ($attempt)..."
            Start-Sleep -Seconds 10
        }
    }
}

Step 'Container Apps environment'
$cae = Json (az containerapp env show -n $Environment -g $EnvironmentResourceGroup `
    --query '{id:id, location:location, ip:properties.staticIp, verification:properties.customDomainConfiguration.customDomainVerificationId}' -o json)
$location = ($cae.location -replace ' ', '').ToLowerInvariant()
Write-Host "  $Environment in $location"

Step 'Resource group'
az group create -n $ResourceGroup -l $location -o none

Step 'Identity the app runs as'
$id = Json (az identity create -n $Identity -g $ResourceGroup -l $location --query '{id:id, clientId:clientId, principalId:principalId}' -o json)

Step 'Storage: database replicas, the key ring and covers'
if (-not (Find storage account show -n $StorageAccount -g $ResourceGroup)) {
    az storage account create -n $StorageAccount -g $ResourceGroup -l $location --sku Standard_LRS --kind StorageV2 `
        --min-tls-version TLS1_2 --allow-blob-public-access false --allow-shared-key-access false -o none
}
$storageId = az storage account show -n $StorageAccount -g $ResourceGroup --query id -o tsv
foreach ($container in 'replica', 'keys', 'covers') {
    az storage container-rm create --storage-account $storageId -n $container -o none
}
Grant $id.principalId ServicePrincipal 'Storage Blob Data Contributor' $storageId

Step 'Key Vault: the key-ring key and sign-in secrets'
if (-not (Find keyvault show -n $KeyVault -g $ResourceGroup)) {
    az keyvault create -n $KeyVault -g $ResourceGroup -l $location --enable-rbac-authorization true -o none
}
$vaultId = az keyvault show -n $KeyVault -g $ResourceGroup --query id -o tsv
$me = az ad signed-in-user show --query id -o tsv
Grant $me User 'Key Vault Administrator' $vaultId
Grant $id.principalId ServicePrincipal 'Key Vault Crypto User' $vaultId
Grant $id.principalId ServicePrincipal 'Key Vault Secrets User' $vaultId
Retry {
    if (-not (Find keyvault key show --vault-name $KeyVault -n dataprotection)) {
        az keyvault key create --vault-name $KeyVault -n dataprotection --kty RSA --size 3072 -o none
    }
}

function SecretExists([string] $name) { [bool] (Find keyvault secret show --vault-name $KeyVault -n $name --query id) }

Step 'Container registry'
$registryId = az acr show -n $Registry --query id -o tsv
Grant $id.principalId ServicePrincipal 'AcrPull' $registryId

Step 'Microsoft sign-in'
$signIn = Find ad app list --display-name 'Bibliotaph' --query "[?displayName=='Bibliotaph'] | [0].{appId:appId, id:id}" -o json
# One registration signs people in and connects their OneDrive, so it answers on both callbacks, here and on a
# developer's machine.
$redirects = foreach ($origin in "https://$Domain", 'https://localhost:5179') {
    "$origin/auth/callback/microsoft"
    "$origin/storage/callback/onedrive"
}
if (-not $signIn) {
    # Personal accounts need v2 tokens, which can only be set once the registration exists.
    $signIn = Json (az ad app create --display-name 'Bibliotaph' --sign-in-audience AzureADMultipleOrgs `
        --web-redirect-uris @redirects --query '{appId:appId, id:id}' -o json)
    az ad app update --id $signIn.id --set api.requestedAccessTokenVersion=2
    az ad app update --id $signIn.id --sign-in-audience AzureADandPersonalMicrosoftAccount
}
else {
    az ad app update --id $signIn.id --web-redirect-uris @redirects
}
if (-not (SecretExists 'auth-microsoft')) {
    $password = az ad app credential reset --id $signIn.id --append --display-name $Domain --years 2 --query password -o tsv
    az keyvault secret set --vault-name $KeyVault -n auth-microsoft --value $password -o none
    Remove-Variable password
}

$providers = @("Auth__microsoft__ClientId=$($signIn.appId)", 'Auth__microsoft__ClientSecret=secretref:auth-microsoft')
$secrets = @("auth-microsoft=keyvaultref:https://$KeyVault.vault.azure.net/secrets/auth-microsoft,identityref:$($id.id)")
if ($GoogleClientId) {
    Step 'Google sign-in'
    if (-not (SecretExists 'auth-google')) {
        $secret = Read-Host "  Client secret for $GoogleClientId" -AsSecureString
        az keyvault secret set --vault-name $KeyVault -n auth-google --value ([System.Net.NetworkCredential]::new('', $secret).Password) -o none
    }
    $providers += "Auth__google__ClientId=$GoogleClientId", 'Auth__google__ClientSecret=secretref:auth-google'
    $secrets += "auth-google=keyvaultref:https://$KeyVault.vault.azure.net/secrets/auth-google,identityref:$($id.id)"
}

$settings = @(
    "REPLICA_URL=abs://$StorageAccount@replica"
    # Litestream finds the user-assigned identity through this; the server is told it directly.
    "AZURE_CLIENT_ID=$($id.clientId)"
    "Server__ManagedIdentityClientId=$($id.clientId)"
    "Server__KeyRingBlob=https://$StorageAccount.blob.core.windows.net/keys/keyring.xml"
    "Server__KeyRingKey=https://$KeyVault.vault.azure.net/keys/dataprotection"
    "Server__CoverContainer=https://$StorageAccount.blob.core.windows.net/covers"
    "Server__PublicOrigin=https://$Domain"
) + $providers

Step 'Container app'
if (-not (Find containerapp show -n $App -g $ResourceGroup --query id)) {
    # The first image is built in Azure, so no Docker is needed here. CI pushes every later one.
    $image = "$Registry.azurecr.io/bibliotaph-server:setup"
    az acr build -r $Registry -t 'bibliotaph-server:setup' -f src/Bibliotaph.Server/Container/Dockerfile $repoRoot
    # One replica, never more and never none: each library's SQLite files have exactly one writer, and the disk
    # under /data is only a working copy that Litestream restores from Blob storage.
    az containerapp create -n $App -g $ResourceGroup --environment $cae.id --image $image `
        --registry-server "$Registry.azurecr.io" --registry-identity $id.id --user-assigned $id.id `
        --ingress external --target-port 8080 --min-replicas 1 --max-replicas 1 --cpu 1 --memory 2Gi `
        --secrets @secrets --env-vars @settings -o none
}
else {
    az containerapp secret set -n $App -g $ResourceGroup --secrets @secrets -o none
    az containerapp update -n $App -g $ResourceGroup --set-env-vars @settings -o none
}
$appId = az containerapp show -n $App -g $ResourceGroup --query id -o tsv
$fqdn = az containerapp show -n $App -g $ResourceGroup --query properties.configuration.ingress.fqdn -o tsv
Write-Host "  running at https://$fqdn"

Step 'Domain'
if ($BindDomain) {
    foreach ($hostname in $Domain, "www.$Domain") {
        $bound = az containerapp hostname list -n $App -g $ResourceGroup --query "[?name=='$hostname'].bindingType" -o tsv
        if ($bound -eq 'SniEnabled') { Write-Host "  $hostname already has its certificate"; continue }
        if (-not $bound) { az containerapp hostname add -n $App -g $ResourceGroup --hostname $hostname -o none }
        az containerapp hostname bind -n $App -g $ResourceGroup --hostname $hostname --environment $cae.id `
            --validation-method HTTP -o none
        Write-Host "  $hostname bound with a managed certificate"
    }
}
else {
    Write-Host "  DNS records for $Domain (then run again with -BindDomain):"
    Write-Host "    A    @           $($cae.ip)"
    Write-Host "    A    www         $($cae.ip)"
    Write-Host "    TXT  asuid       $($cae.verification)"
    Write-Host "    TXT  asuid.www   $($cae.verification)"
}

Step 'GitHub Actions deploy identity'
$deploy = Find ad app list --display-name 'Bibliotaph deploy' --query "[?displayName=='Bibliotaph deploy'] | [0].{appId:appId, id:id}" -o json
if (-not $deploy) {
    $deploy = Json (az ad app create --display-name 'Bibliotaph deploy' --query '{appId:appId, id:id}' -o json)
}
$deployPrincipal = Find ad sp show --id $deploy.appId --query id -o json
if (-not $deployPrincipal) { $deployPrincipal = Json (az ad sp create --id $deploy.appId --query id -o json) }
$subject = "repo:${GitHubRepository}:ref:refs/heads/main"
$federated = az ad app federated-credential list --id $deploy.id --query "[?subject=='$subject'] | length(@)" -o tsv
if ($federated -eq '0') {
    $file = New-TemporaryFile
    @{ name = 'main'; issuer = 'https://token.actions.githubusercontent.com'; subject = $subject; audiences = @('api://AzureADTokenExchange') } |
        ConvertTo-Json | Set-Content $file
    az ad app federated-credential create --id $deploy.id --parameters "@$file" -o none
    Remove-Item $file
}
# Push images, and roll the one app onto them; nothing else. Changing an app also needs the right to join its
# environment, which no built-in role grants without much more besides.
Grant $deployPrincipal ServicePrincipal 'AcrPush' $registryId
Grant $deployPrincipal ServicePrincipal 'Contributor' $appId
$joinRole = 'Container Apps environment join'
if (-not (Find role definition list --name $joinRole --query '[0].id')) {
    $file = New-TemporaryFile
    @{ Name = $joinRole; Description = 'Deploy a container app into an existing environment.'
       Actions = @('Microsoft.App/managedEnvironments/join/action', 'Microsoft.App/managedEnvironments/read')
       AssignableScopes = @("/subscriptions/$(az account show --query id -o tsv)") } | ConvertTo-Json | Set-Content $file
    az role definition create --role-definition "@$file" -o none
    Remove-Item $file
}
Retry { Grant $deployPrincipal ServicePrincipal $joinRole $cae.id }

$account = Json (az account show --query '{tenant:tenantId, subscription:id}' -o json)
Write-Host "`nSet these repository variables so every push to main deploys (no secrets are involved):"
Write-Host "  gh variable set AZURE_CLIENT_ID --body $($deploy.appId) -R $GitHubRepository"
Write-Host "  gh variable set AZURE_TENANT_ID --body $($account.tenant) -R $GitHubRepository"
Write-Host "  gh variable set AZURE_SUBSCRIPTION_ID --body $($account.subscription) -R $GitHubRepository"
Write-Host "  gh variable set AZURE_REGISTRY --body $Registry -R $GitHubRepository"
Write-Host "  gh variable set AZURE_RESOURCE_GROUP --body $ResourceGroup -R $GitHubRepository"
Write-Host "  gh variable set AZURE_CONTAINER_APP --body $App -R $GitHubRepository"
