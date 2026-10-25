# ---- Frontend ----
FROM node:20-alpine AS web
WORKDIR /web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npx tsc -b && npx vite build --outDir /out/wwwroot

# ---- API ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/SecondHalfMind.Api/SecondHalfMind.Api.csproj -c Release -o /app
COPY --from=web /out/wwwroot /app/wwwroot

# ---- Runtime: minimal image, non-root user ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    Calibration__Path=/app/data/calibration.json
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SecondHalfMind.Api.dll"]
