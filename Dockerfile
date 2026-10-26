# The bridge API with the operations console. Production: App Service runs the same image or the
# published output. Locally: docker compose up (demo world on Apache Kafka).
FROM node:22-bookworm-slim AS console
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
COPY contracts/ contracts/
COPY dataverse/ dataverse/
RUN dotnet publish src/Bridge.Api -c Release -o /app
COPY --from=console /web/dist/ /app/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s CMD ["dotnet", "Bridge.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Bridge.Api.dll"]
