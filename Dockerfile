# Dockerfile для Render.com / Railway / Fly.io
# Использует .NET 8 SDK для сборки и runtime для запуска

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем проект и восстанавливаем пакеты
COPY pmo_nav.csproj ./
RUN dotnet restore

# Копируем весь код и собираем
COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Runtime ────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Устанавливаем культуру для корректного отображения дат
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV LANG=C.UTF-8

COPY --from=build /app/publish ./

# Порт, который слушает приложение (Render/Railway пробрасывают сюда)
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "pmo_nav.dll"]
