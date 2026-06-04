# ── Stage 1: Build React frontend ────────────────────────────────────────────
FROM node:22-alpine AS frontend
WORKDIR /app/ClientApp
COPY ClientApp/package*.json ./
RUN npm ci
COPY ClientApp/ ./
RUN npm run build

# ── Stage 2: Build .NET app ───────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . ./
COPY --from=frontend /app/ClientApp/dist ./wwwroot
RUN dotnet publish -c Release -o /out

# ── Stage 3: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
EXPOSE 3003
ENV ASPNETCORE_URLS=http://+:3003
ENTRYPOINT ["dotnet", "EHealth.Hospital.Api.dll"]
