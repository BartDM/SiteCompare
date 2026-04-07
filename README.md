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

```bash
ACR_PASSWORD=$(az acr credential show --name <acr-name> --query "passwords[0].value" -o tsv)

az container create \
  --resource-group <resource-group> \
  --name sitecompare \
  --image <acr-name>.azurecr.io/sitecompare:latest \
  --registry-login-server <acr-name>.azurecr.io \
  --registry-username <acr-name> \
  --registry-password "$ACR_PASSWORD" \
  --ports 8080 \
  --dns-name-label <unique-dns-label> \
  --cpu 1 \
  --memory 2
```

De applicatie is bereikbaar op `http://<unique-dns-label>.<region>.azurecontainer.io:8080`.

### 6 – Nieuwe versie uitrollen

```bash
# Nieuwe image bouwen en pushen
docker build -t <acr-name>.azurecr.io/sitecompare:latest .
docker push <acr-name>.azurecr.io/sitecompare:latest

# App Service herstarten om de nieuwe image op te halen
az webapp restart --name <app-name> --resource-group <resource-group>
```

### Opmerking: Playwright en Chromium

De container is gebaseerd op het officiële **Playwright .NET**-image  
(`mcr.microsoft.com/playwright/dotnet:v1.58.0-noble`) dat Chromium en alle  
benodigde systeembibliotheken al bevat. Er zijn geen extra stappen vereist.
