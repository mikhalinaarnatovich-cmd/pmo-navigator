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

# Копируем скомпилированное приложение
COPY --from=build /app/publish ./

# Копируем SQL-миграцию и данные проектов (нужны при запуске)
COPY --from=build /src/Database/ ./Database/
COPY --from=build /src/wwwroot/ ./wwwroot/

# Тестовые документы проектов — копируем напрямую в обход dotnet publish
# (в именах файлов есть ';' и скобки, MSBuild на них падает с Conflicting assets)
COPY ProjectDocs ./wwwroot/test-docs

# Render передаёт строку подключения через переменную окружения
# ConnectionStrings__PmoNavigatorDb
# Авто-миграция выполнится при старте (см. Program.cs)

# Порт, который слушает приложение (Render пробрасывает сюда)
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "pmo_nav.dll"]
