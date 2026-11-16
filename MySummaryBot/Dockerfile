FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["MySummaryBot.csproj", "./"]
RUN dotnet restore "MySummaryBot.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "MySummaryBot.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "MySummaryBot.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish .
COPY SaveBotWorker/ /app/savebot/
RUN if [ -f /app/savebot/source/requirements.txt ]; then \
        apt-get update \
        && apt-get install -y --no-install-recommends ca-certificates ffmpeg fonts-dejavu-core python3 python3-venv \
        && rm -rf /var/lib/apt/lists/* \
        && python3 -m venv /opt/savebot \
        && /opt/savebot/bin/pip install --no-cache-dir -r /app/savebot/source/requirements.txt \
        && /opt/savebot/bin/python -B -c "import sys; sys.path.insert(0, '/app/savebot'); import worker"; \
    fi
ENTRYPOINT ["dotnet", "MySummaryBot.dll"]
