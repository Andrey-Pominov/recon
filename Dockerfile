FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Recon.slnx ./
COPY src/Recon.Core/Recon.Core.csproj src/Recon.Core/
COPY src/Recon.Api/Recon.Api.csproj   src/Recon.Api/
RUN dotnet restore src/Recon.Api/Recon.Api.csproj
COPY db/ db/
COPY src/ src/
RUN dotnet publish src/Recon.Api/Recon.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Npgsql probes for Kerberos on connect; without this library it logs an error on every start.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Recon.Api.dll"]
