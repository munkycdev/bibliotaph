<#
.SYNOPSIS
    Creates or updates everything bibliotaph.online runs on in Azure.

.DESCRIPTION
    Run it from the repository root, signed in with az login to the subscription that holds the Container Apps
    environment. Every step checks what's already there, so running it again only fills in what's missing and refreshes
    the app's settings.

    It makes, in its own resource group: a user-assigned identity the app runs as, a storage account (Litestream's
    replica, the Data Protection key ring, covers and weekly backups), a Key Vault (the key that wraps the key ring, and the client
    secrets), the Microsoft app registration OneDrive is connected through, the container app (one replica, never
    scaled), and the app registration GitHub Actions deploys with. It reuses an existing Container Apps environment.

    People sign in through Auth0. Its tenant and Regular Web Application are made in Auth0's dashboard, with the URLs
    this script prints; then -Auth0Domain and -Auth0ClientId set them here, and the client secret is asked for once.

    The image comes from GitHub's container registry, where CI pushes one for every push to main. The app pulls it
    without signing in, so the package must be public: on GitHub, the package's settings, Change visibility.

    The names of the existing environment and its group are listed by:
        az containerapp env list --query "[].{name:name, group:resourceGroup}" -o table

.EXAMPLE
    ./infra/setup.ps1 -Environment <environment> -EnvironmentResourceGroup <environment's group>

.EXAMPLE
    # Once the DNS records it printed have propagated:
    ./infra/setup.ps1 -Environment <environment> -EnvironmentResourceGroup <environment's group> -BindDomain
#>
#Requires -Version 7.3
[CmdletBinding()]
param(
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
    # The image CI pushes to GitHub's container registry, without its tag.
    [string] $Image = 'ghcr.io/munkycdev/bibliotaph-server',

    # The Auth0 tenant's domain (its custom domain, such as auth.bibliotaph.online, once that is verified, or else
    # bibliotaph.us.auth0.com) and its Regular Web Application's client ID, from the application's Settings in Auth0's
    # dashboard. Its client secret is asked for once and kept in Key Vault, and asked for again when the client ID
    # changes. Once set, a later run without them keeps them.
    [string] $Auth0Domain,
    [string] $Auth0ClientId,

    # The Dropbox app's App key, from its Settings in Dropbox's App Console. Its App secret is asked for once and kept
    # in Key Vault. Once set, a later run without it keeps it.
    [string] $DropboxAppKey,

    # The Google Cloud OAuth client's Client ID (a Web application client). Its client secret is asked for once and kept
    # in Key Vault. Once set, a later run without it keeps it.
    [string] $GoogleClientId,

    # The Box app's Client ID, from its Configuration in Box's Developer Console. Its client secret is asked for once
    # and kept in Key Vault. Once set, a later run without it keeps it.
    [string] $BoxClientId,

    # Adds the domain and www to the app and issues managed certificates. Needs the DNS records in place first.
    [switch] $BindDomain
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Json { $args[0] | ConvertFrom-Json }

# az prints "not found" errors for things that don't exist yet; those return $null here instead of stopping the script.
function Find {
    $PSNativeCommandUseErrorActionPreference = $false
    $result = az @args 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $result) { return $null }
    $result | ConvertFrom-Json
}

# Asks GitHub's registry for the image as anyone would, with an anonymous pull token.
function ImageIsPullable([string] $reference) {
    $name, $tag = ($reference -replace '^ghcr\.io/', '') -split ':', 2
    try {
        $token = (Invoke-RestMethod "https://ghcr.io/token?scope=repository:${name}:pull").token
        $accept = 'application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json'
        Invoke-WebRequest -Method Head "https://ghcr.io/v2/$name/manifests/$tag" -Headers @{ Authorization = "Bearer $token"; Accept = $accept } | Out-Null
        return $true
    }
    catch { return $false }
}

# az on Windows is a batch file, so cmd reads its arguments first: a query with brackets or pipes but no spaces isn't
# quoted, and cmd stops at them. Those filters are done here instead.
function Grant([string] $principalId, [string] $principalType, [string] $role, [string] $scope) {
    $existing = Json (az role assignment list --assignee $principalId --role $role --scope $scope -o json)
    if ($existing) { return }
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

Step 'Storage: database replicas, the key ring, covers and backups'
if (-not (Find storage account show -n $StorageAccount -g $ResourceGroup)) {
    az storage account create -n $StorageAccount -g $ResourceGroup -l $location --sku Standard_LRS --kind StorageV2 `
        --min-tls-version TLS1_2 --allow-blob-public-access false --allow-shared-key-access false -o none
}
$storageId = az storage account show -n $StorageAccount -g $ResourceGroup --query id -o tsv
foreach ($container in 'replica', 'keys', 'covers', 'backups') {
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

Step 'OneDrive app registration'
$signIn = Find ad app list --display-name 'Bibliotaph' --query "[?displayName=='Bibliotaph'] | [0].{appId:appId, id:id}" -o json
# It connects people's OneDrive, here and on a developer's machine; signing in is Auth0's.
$redirects = foreach ($origin in "https://$Domain", 'https://localhost:5179') { "$origin/storage/callback/onedrive" }
if (-not $signIn) {
    $signIn = Json (az ad app create --display-name 'Bibliotaph' --sign-in-audience AzureADMultipleOrgs `
        --web-redirect-uris @redirects --query '{appId:appId, id:id}' -o json)
}
else {
    az ad app update --id $signIn.id --web-redirect-uris @redirects
}
# Personal accounts need v2 tokens, which can only be set once the registration exists. A new registration has no api
# section for az ad app update --set to change, so this goes to Graph, with the body in a file to keep it from cmd.
function PatchSignIn([hashtable] $change) {
    $file = New-TemporaryFile
    $change | ConvertTo-Json -Depth 3 | Set-Content $file
    az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$($signIn.id)" --headers 'Content-Type=application/json' `
        --body "@$file" -o none
    Remove-Item $file
}
$registration = Json (az ad app show --id $signIn.id --query '{version:api.requestedAccessTokenVersion, audience:signInAudience}' -o json)
if ($registration.version -ne 2) { PatchSignIn @{ api = @{ requestedAccessTokenVersion = 2 } } }
if ($registration.audience -ne 'AzureADandPersonalMicrosoftAccount') { PatchSignIn @{ signInAudience = 'AzureADandPersonalMicrosoftAccount' } }
if (-not (SecretExists 'auth-microsoft')) {
    $password = az ad app credential reset --id $signIn.id --append --display-name $Domain --years 2 --query password -o tsv
    az keyvault secret set --vault-name $KeyVault -n auth-microsoft --value $password -o none
    Remove-Variable password
}

# The secret keeps the name it had when this registration signed people in too.
$providers = @("Storage__OneDrive__ClientId=$($signIn.appId)", 'Storage__OneDrive__ClientSecret=secretref:auth-microsoft')
$secrets = @("auth-microsoft=keyvaultref:https://$KeyVault.vault.azure.net/secrets/auth-microsoft,identityref:$($id.id)")

Step 'Auth0 sign-in'
$auth0Secret = $null
if ($Auth0Domain -or $Auth0ClientId) {
    if (-not ($Auth0Domain -and $Auth0ClientId)) { throw 'Give both -Auth0Domain and -Auth0ClientId.' }
    $Auth0Domain = $Auth0Domain -replace '^https://', '' -replace '/+$', ''
    $knownClient = @(Find containerapp show -n $App -g $ResourceGroup --query 'properties.template.containers[0].env' -o json |
        Where-Object name -eq 'Auth__auth0__ClientId').value
    # A different application, as when sign-in moves to another tenant, has its own secret.
    if (-not (SecretExists 'auth0') -or ($knownClient -and $knownClient -ne $Auth0ClientId)) {
        $secret = Read-Host "  Client secret for $Auth0ClientId" -AsSecureString
        # The app would pick up a new version by itself only within half an hour, after the new client ID is already in
        # use, so this run points it at the version just written.
        $auth0Secret = az keyvault secret set --vault-name $KeyVault -n auth0 --value ([System.Net.NetworkCredential]::new('', $secret).Password) --query id -o tsv
    }
    $providers += "Auth__auth0__Domain=$Auth0Domain", "Auth__auth0__ClientId=$Auth0ClientId", 'Auth__auth0__ClientSecret=secretref:auth0'
}
# The secret reference stays while the app's settings name it, which a run without the switches leaves as they were.
if (-not $auth0Secret -and (SecretExists 'auth0')) { $auth0Secret = "https://$KeyVault.vault.azure.net/secrets/auth0" }
if ($auth0Secret) { $secrets += "auth0=keyvaultref:$auth0Secret,identityref:$($id.id)" }
Write-Host "  In Auth0, the application's Allowed Callback URLs: https://$Domain/auth/callback/auth0, https://localhost:5179/auth/callback/auth0"
Write-Host "  Allowed Logout URLs: https://$Domain/, https://localhost:5179/"
if (-not $Auth0Domain -and -not (SecretExists 'auth0')) {
    Write-Host '  Nobody can sign in until this runs with -Auth0Domain and -Auth0ClientId.' -ForegroundColor Yellow
}

Step 'Dropbox'
# A storage app's settings and secret, kept as Auth0's are: the ID is given once, the secret asked for once.
function StorageApp([string] $name, [string] $section, [string] $clientId, [string] $prompt) {
    $secretName = "storage-$($name.ToLowerInvariant())"
    $known = @(Find containerapp show -n $App -g $ResourceGroup --query 'properties.template.containers[0].env' -o json |
        Where-Object name -eq "Storage__${section}__ClientId").value
    if (-not $clientId) { $clientId = $known }
    if (-not $clientId) { return $false }
    if (-not (SecretExists $secretName)) {
        $secret = Read-Host "  $prompt" -AsSecureString
        az keyvault secret set --vault-name $KeyVault -n $secretName --value ([System.Net.NetworkCredential]::new('', $secret).Password) -o none
    }
    $script:providers += "Storage__${section}__ClientId=$clientId", "Storage__${section}__ClientSecret=secretref:$secretName"
    $script:secrets += "$secretName=keyvaultref:https://$KeyVault.vault.azure.net/secrets/$secretName,identityref:$($id.id)"
    return $true
}
if (-not (StorageApp 'Dropbox' 'Dropbox' $DropboxAppKey "App secret for Dropbox app $DropboxAppKey")) {
    Write-Host '  Dropbox can be connected once this runs with -DropboxAppKey.' -ForegroundColor Yellow
}
Write-Host "  In Dropbox's App Console: Scoped access, Full Dropbox; permissions account_info.read, files.metadata.read, files.content.read"
Write-Host "  Redirect URI https://$Domain/storage/callback/dropbox, Webhook URI https://$Domain/storage/notifications/dropbox"

Step 'Google Drive'
if (-not (StorageApp 'GoogleDrive' 'GoogleDrive' $GoogleClientId "Client secret for Google OAuth client $GoogleClientId")) {
    Write-Host '  Google Drive can be connected once this runs with -GoogleClientId.' -ForegroundColor Yellow
}
Write-Host "  In Google Cloud: enable the Google Drive API; OAuth consent screen with the scope .../auth/drive.readonly, published"
Write-Host "  (In production), since a Testing app's sign-ins lapse after 7 days. Web client redirect URI https://$Domain/storage/callback/googledrive"

Step 'Box'
if (-not (StorageApp 'Box' 'Box' $BoxClientId "Client secret for Box app $BoxClientId")) {
    Write-Host '  Box can be connected once this runs with -BoxClientId.' -ForegroundColor Yellow
}
Write-Host "  In Box's Developer Console: a Platform App with User Authentication (OAuth 2.0); application scope"
Write-Host "  'Read all files and folders stored in Box' only; Redirect URI https://$Domain/storage/callback/box"

$settings = @(
    "REPLICA_URL=abs://$StorageAccount@replica"
    # Litestream finds the user-assigned identity through this; the server is told it directly.
    "AZURE_CLIENT_ID=$($id.clientId)"
    "Server__ManagedIdentityClientId=$($id.clientId)"
    "Server__KeyRingBlob=https://$StorageAccount.blob.core.windows.net/keys/keyring.xml"
    "Server__KeyRingKey=https://$KeyVault.vault.azure.net/keys/dataprotection"
    "Server__CoverContainer=https://$StorageAccount.blob.core.windows.net/covers"
    "Server__BackupContainer=https://$StorageAccount.blob.core.windows.net/backups"
    "Server__PublicOrigin=https://$Domain"
) + $providers

Step 'Container app'
if (-not (Find containerapp show -n $App -g $ResourceGroup --query id)) {
    # The first image is the one CI pushed for main; each later push to main rolls the app onto its own.
    $image = "${Image}:main"
    if (-not (ImageIsPullable $image)) {
        throw "$image can't be pulled without signing in. Push to main so CI publishes it, then make the package public on GitHub (its settings, Change visibility)."
    }
    # One replica, never more and never none: each library's SQLite files have exactly one writer, and the disk
    # under /data is only a working copy that Litestream restores from Blob storage.
    az containerapp create -n $App -g $ResourceGroup --environment $cae.id --image $image --user-assigned $id.id `
        --ingress external --target-port 8080 --min-replicas 1 --max-replicas 1 --cpu 1 --memory 2Gi `
        --secrets @secrets --env-vars @settings -o none
}
else {
    az containerapp secret set -n $App -g $ResourceGroup --secrets @secrets -o none
    az containerapp update -n $App -g $ResourceGroup --set-env-vars @settings -o none
    # Microsoft and Google signed people in directly before Auth0 did.
    $old = @(az containerapp show -n $App -g $ResourceGroup --query 'properties.template.containers[0].env[].name' -o json | ConvertFrom-Json |
        Where-Object { $_ -like 'Auth__microsoft__*' -or $_ -like 'Auth__google__*' })
    if ($old) { az containerapp update -n $App -g $ResourceGroup --remove-env-vars @old -o none }
}
$appId = az containerapp show -n $App -g $ResourceGroup --query id -o tsv
$fqdn = az containerapp show -n $App -g $ResourceGroup --query properties.configuration.ingress.fqdn -o tsv
Write-Host "  running at https://$fqdn"

Step 'Domain'
if ($BindDomain) {
    foreach ($hostname in $Domain, "www.$Domain") {
        $bound = (Json (az containerapp hostname list -n $App -g $ResourceGroup -o json) | Where-Object name -eq $hostname).bindingType
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
# GitHub's tokens may name the repository with its owner's and its own IDs ("repo:owner@1/name@2:...") rather than by
# name alone, so main is trusted under both spellings. The IDs also keep a renamed or re-created repository out.
$repo = Json (gh api "repos/$GitHubRepository")
$owner, $name = $GitHubRepository.Split('/')
$subjects = [ordered]@{
    'main' = "repo:${GitHubRepository}:ref:refs/heads/main"
    'main-ids' = "repo:$owner@$($repo.owner.id)/$name@$($repo.id):ref:refs/heads/main"
}
foreach ($credential in $subjects.GetEnumerator()) {
    $federated = az ad app federated-credential list --id $deploy.id --query "[?subject=='$($credential.Value)'] | length(@)" -o tsv
    if ($federated -ne '0') { continue }
    $file = New-TemporaryFile
    @{ name = $credential.Key; issuer = 'https://token.actions.githubusercontent.com'; subject = $credential.Value; audiences = @('api://AzureADTokenExchange') } |
        ConvertTo-Json | Set-Content $file
    az ad app federated-credential create --id $deploy.id --parameters "@$file" -o none
    Remove-Item $file
}
# Roll the one app onto a new image; nothing else. Changing an app also needs the right to join its environment,
# which no built-in role grants without much more besides.
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
Write-Host "  gh variable set AZURE_RESOURCE_GROUP --body $ResourceGroup -R $GitHubRepository"
Write-Host "  gh variable set AZURE_CONTAINER_APP --body $App -R $GitHubRepository"
