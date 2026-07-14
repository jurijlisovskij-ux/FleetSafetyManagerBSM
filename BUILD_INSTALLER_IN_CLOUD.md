# Как получить готовый Setup.exe без установки Node.js и .NET SDK

Сборка выполняется автоматически на сервере GitHub Actions.

## Один раз

1. Создайте пустой приватный репозиторий GitHub.
2. Загрузите в него всё содержимое этой папки.
3. Откройте вкладку **Actions**.
4. Выберите **Build Fleet Safety Manager Windows Installers**.
5. Нажмите **Run workflow**.
6. После завершения скачайте artifact:

```text
FleetSafetyManager-Windows-Installers
```

Внутри будут:

```text
FleetSafetyManager_Office_Setup.exe
FleetSafetyManager_Ship_Setup.exe
```

На сервере и судовых ПК не нужны:

- Docker;
- Node.js;
- .NET SDK;
- PostgreSQL.

Установщики содержат self-contained .NET-приложение.

## Важно

RC2 создаёт два установщика и локальную SQLite-базу. Полноценная передача
`.fsmsync` между Office и Ship будет добавлена следующим этапом.
