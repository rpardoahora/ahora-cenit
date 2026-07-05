# Imagen única: la API (.NET) sirve también los estáticos del frontend (Vite)
# desde wwwroot, en el mismo puerto. Contexto de build: app/ (esta carpeta).

# --- Stage 1: build del frontend ---
FROM node:22-alpine AS frontend-build
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json* ./
RUN npm ci
COPY frontend/ .
ENV VITE_API_URL=/api
RUN npm run build

# --- Stage 2: build del backend ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY backend/AhoraCenit.sln ./
COPY backend/AhoraCenit.Api/AhoraCenit.Api.csproj AhoraCenit.Api/
RUN dotnet restore AhoraCenit.sln
COPY backend/AhoraCenit.Api/ AhoraCenit.Api/
RUN dotnet publish AhoraCenit.Api/AhoraCenit.Api.csproj -c Release -o /app/publish --no-restore

# --- Stage 3: runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=backend-build /app/publish .
COPY --from=frontend-build /src/frontend/dist ./wwwroot

ENTRYPOINT ["dotnet", "AhoraCenit.Api.dll"]
