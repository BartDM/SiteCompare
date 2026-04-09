# SiteCompare
Vergelijk 2 sites

## Docker

### Lokaal bouwen en uitvoeren

```bash
# Image bouwen
docker build -t sitecompare .

# Container starten (poort 8080)
docker run -p 8080:8080 sitecompare
```

Open de applicatie op `http://localhost:8080`.

---

## Uitrollen op Azure

De stappen hieronder gebruiken de **Azure CLI** (`az`).  
Vervang `<...>` door jouw eigen waarden.

### 1 – Vereisten

| Vereiste | Versie |
|---|---|
| [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) | ≥ 2.60 |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | ≥ 24 |
| Azure-abonnement | – |

Aanmelden:

```bash
az login
az account set --subscription "<subscription-id>"
```

### 2 – Resource group aanmaken

```bash
az group create \
  --name <resource-group> \
  --location westeurope
```

### 3 – Azure Container Registry (ACR) aanmaken

```bash
az acr create \
  --resource-group <resource-group> \
  --name <acr-name> \
  --sku Basic \
  --admin-enabled true
```

### 4 – Image bouwen en pushen naar ACR

```bash
# Aanmelden bij ACR
az acr login --name <acr-name>

# Image taggen met de ACR-loginserver
docker build -t <acr-name>.azurecr.io/sitecompare:latest .

# Image pushen
docker push <acr-name>.azurecr.io/sitecompare:latest
```

> **Alternatief** – Laat Azure de image zelf bouwen (geen lokale Docker vereist):
>
> ```bash
> az acr build \
>   --registry <acr-name> \
>   --image sitecompare:latest .
> ```

### 5a – Uitrollen als Azure App Service (Web App for Containers)

Dit is de aanbevolen optie voor een productie-webapplicatie.

```bash
# App Service Plan aanmaken (Linux)
az appservice plan create \
  --name <plan-name> \
  --resource-group <resource-group> \
  --is-linux \
  --sku B2

# Web App aanmaken
az webapp create \
  --name <app-name> \
  --resource-group <resource-group> \
  --plan <plan-name> \
  --deployment-container-image-name <acr-name>.azurecr.io/sitecompare:latest

# Web App koppelen aan ACR (beheerde identiteit)
az webapp config container set \
  --name <app-name> \
  --resource-group <resource-group> \
  --container-image-name <acr-name>.azurecr.io/sitecompare:latest \
  --container-registry-url https://<acr-name>.azurecr.io

# ACR-referenties ophalen en configureren
ACR_PASSWORD=$(az acr credential show --name <acr-name> --query "passwords[0].value" -o tsv)
az webapp config container set \
  --name <app-name> \
  --resource-group <resource-group> \
  --container-registry-user <acr-name> \
  --container-registry-password "$ACR_PASSWORD"

# Poort instellen
az webapp config appsettings set \
  --name <app-name> \
  --resource-group <resource-group> \
  --settings WEBSITES_PORT=8080
```

De applicatie is bereikbaar op `https://<app-name>.azurewebsites.net`.

### 5b – Uitrollen als Azure Container Instance (ACI)

Geschikt voor eenvoudige of tijdelijke omgevingen.  
Deze aanpak gebruikt een **beheerde identiteit** (managed identity) – geen wachtwoorden of geheimen nodig.

De gevoelige verbindingsstrings (`AzureStorage:ConnectionString`, `Redis:ConnectionString`) worden **nooit als environment variable** doorgegeven.  
Ze worden veilig opgeslagen in **Azure Key Vault** en door de applicatie opgehaald via de managed identity.

#### Stap 1 – Beheerde identiteit aanmaken en koppelen aan ACR

```bash
# User-assigned managed identity aanmaken
az identity create \
  --name sitecompare-identity \
  --resource-group <resource-group>

# Benodigde ID's ophalen
IDENTITY_ID=$(az identity show \
  --name sitecompare-identity \
  --resource-group <resource-group> \
  --query id -o tsv)

IDENTITY_PRINCIPAL=$(az identity show \
  --name sitecompare-identity \
  --resource-group <resource-group> \
  --query principalId -o tsv)

ACR_ID=$(az acr show \
  --name <acr-name> \
  --query id -o tsv)

# AcrPull-rol toewijzen aan de identiteit (leesrecht op de registry)
az role assignment create \
  --assignee "$IDENTITY_PRINCIPAL" \
  --role AcrPull \
  --scope "$ACR_ID"
```

#### Stap 2 – Key Vault aanmaken en secrets opslaan

```bash
# Key Vault aanmaken
az keyvault create \
  --name <keyvault-name> \
  --resource-group <resource-group> \
  --location westeurope

# Verbindingsstrings opslaan als secrets
# Let op: gebruik dubbele koppeltekens (--) als scheidingsteken voor ASP.NET Core configuratiehiërarchie
az keyvault secret set \
  --vault-name <keyvault-name> \
  --name "AzureStorage--ConnectionString" \
  --value "<storage-connection-string>"

az keyvault secret set \
  --vault-name <keyvault-name> \
  --name "Redis--ConnectionString" \
  --value "<redis-connection-string>"
```

#### Stap 3 – Managed identity leesrechten geven op Key Vault

```bash
KV_ID=$(az keyvault show \
  --name <keyvault-name> \
  --query id -o tsv)

# Key Vault Secrets User-rol toewijzen (leesrecht op secrets)
az role assignment create \
  --assignee "$IDENTITY_PRINCIPAL" \
  --role "Key Vault Secrets User" \
  --scope "$KV_ID"
```

#### Stap 4 – Container aanmaken met beheerde identiteit

De enige environment variable die doorgegeven wordt is de **niet-gevoelige** Key Vault URL.  
De applicatie haalt de verbindingsstrings automatisch op bij het opstarten.

```bash
KV_URL=$(az keyvault show \
  --name <keyvault-name> \
  --query properties.vaultUri -o tsv)

az container create \
  --resource-group <resource-group> \
  --name sitecompare \
  --image <acr-name>.azurecr.io/sitecompare:latest \
  --assign-identity "$IDENTITY_ID" \
  --acr-identity "$IDENTITY_ID" \
  --ports 8080 \
  --dns-name-label <unique-dns-label> \
  --cpu 1 \
  --memory 2 \
  --environment-variables KeyVault__Url="$KV_URL"
```

De applicatie is bereikbaar op `http://<unique-dns-label>.<region>.azurecontainer.io:8080`.

> **Opmerking** – ACR admin hoeft **niet** ingeschakeld te zijn voor deze aanpak. De beheerde identiteit regelt de authenticatie via Azure RBAC.

### 6 – Nieuwe versie uitrollen

#### Stap 1 – Nieuwe image bouwen en naar ACR pushen

```bash
# Image bouwen en pushen via ACR (geen lokale Docker vereist)
az acr build \
  --registry <acr-name> \
  --image sitecompare:latest .
```

#### Stap 2a – App Service bijwerken (voor stap 5a)

```bash
# App Service herstarten om de nieuwe image op te halen
az webapp restart --name <app-name> --resource-group <resource-group>
```

#### Stap 2b – ACI bijwerken (voor stap 5b)

ACI ondersteunt geen in-place image-update. De container moet opnieuw worden aangemaakt:

```bash
# Bestaande container verwijderen
az container delete \
  --name sitecompare \
  --resource-group <resource-group> \
  --yes

# Identiteits-ID opnieuw ophalen (of sla deze op als variabele)
IDENTITY_ID=$(az identity show \
  --name sitecompare-identity \
  --resource-group <resource-group> \
  --query id -o tsv)

# Container opnieuw aanmaken met de nieuwe image
az container create \
  --resource-group <resource-group> \
  --name sitecompare \
  --image <acr-name>.azurecr.io/sitecompare:latest \
  --assign-identity "$IDENTITY_ID" \
  --acr-identity "$IDENTITY_ID" \
  --ports 8080 \
  --dns-name-label <unique-dns-label> \
  --cpu 1 \
  --memory 2 \
  --environment-variables KeyVault__Url="$KV_URL"
```

### Opmerking: Playwright en Chromium

De container is gebaseerd op het officiële **Playwright .NET**-image  
(`mcr.microsoft.com/playwright/dotnet:v1.58.0-noble`) dat Chromium en alle  
benodigde systeembibliotheken al bevat. Er zijn geen extra stappen vereist.
