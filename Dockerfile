# ── Build stage ──
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Clone demofile-net main branch (has working HttpBroadcastReader)
RUN apt-get update && apt-get install -y git && \
    git clone https://github.com/saul/demofile-net.git /src/demofile-net

# Copy our project
COPY *.csproj ./app/
WORKDIR /src/app
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /out/publish

# ── Runtime stage ──
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5000

ENTRYPOINT ["dotnet", "GotvPlusServer.dll"]
