# EfCore.JsonSeeding

Заполнение базы данных EF Core из JSON-файлов.

- **Один файл — одна таблица.** Имя файла совпадает с таблицей (`Products.json`) или классом сущности (`Product.json`).
- **Новая таблица = новый файл.** Код менять не нужно: таблицы, ключи и порядок загрузки берутся из модели EF.
- **Порядок по внешним ключам.** `Categories` загружается раньше `Products`, независимо от имён файлов.
- **Повторный запуск безопасен.** Запись с таким же первичным ключом повторно не добавляется.
- **Опечатки видны сразу.** Неизвестное поле (`"pirce"`) или файл без таблицы — ошибка с именем файла и номером записи.
- **PostgreSQL:** после загрузки счётчики id сдвигаются, чтобы обычные вставки не получали занятые id.

Работает поверх встроенного в EF Core механизма `UseSeeding` / `UseAsyncSeeding`, поэтому заполнение
запускается автоматически после `Database.Migrate()` / `MigrateAsync()`, `EnsureCreated()` и `dotnet ef database update`.

## Подключение

1. Ссылка на проект:

   ```xml
   <ProjectReference Include="..\EfCore.JsonSeeding\EfCore.JsonSeeding.csproj" />
   ```

2. JSON-файлы — встроенными ресурсами (тогда они найдутся и при запуске приложения, и в `dotnet ef`):

   ```xml
   <ItemGroup>
     <EmbeddedResource Include="Data\Seed\**\*.json" />
   </ItemGroup>
   ```

3. `Program.cs`:

   ```csharp
   using EfCore.JsonSeeding;

   builder.Services.AddDbContext<AppDbContext>(options => options
       .UseNpgsql(connectionString)
       .UseJsonSeeding(seed => seed
           .AddEmbeddedResources(typeof(Program).Assembly, "Data/Seed/Reference")
           .AddEmbeddedResources(typeof(Program).Assembly, "Data/Seed/Demo",
               enabled: builder.Environment.IsDevelopment())
           .Transform<User>("password", (user, value) =>
           {
               var (hash, salt) = Encryptor.HashPassword(value.GetString()!);
               user.PasswordHash = hash;
               user.PasswordSalt = salt;
           })));
   ```

   Чтобы при разработке база создавалась и заполнялась сама при запуске:

   ```csharp
   if (app.Environment.IsDevelopment())
   {
       using var scope = app.Services.CreateScope();
       await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
   }
   ```

## Формат файлов

Массив объектов. Поля называются как свойства сущности, регистр не важен. Первичный ключ обязателен,
связи — через внешние ключи.

```json
// Data/Seed/Demo/Products.json
[
  { "id": 1, "name": "Яблоко", "categoryId": 1, "price": 25, "imagePath": "image/apple.png" },
  { "id": 2, "name": "Порошок", "categoryId": 3, "price": 899, "isActive": false },
]
```

- Разрешены комментарии `//` и запятая после последнего элемента.
- Перечисления (`enum`) — строкой (`"Hidden"`) или числом.
- Поля, которых нет в JSON, получают значения по умолчанию из класса сущности.

## Настройки

| Метод | Что делает |
|---|---|
| `AddEmbeddedResources(assembly, folder, enabled)` | Файлы из папки проекта, встроенные в сборку. Источники обрабатываются в порядке добавления |
| `AddDirectory(path, enabled)` | Файлы из папки на диске (относительный путь — от папки приложения) |
| `Transform<T>(jsonField, (entity, value) => ...)` | Особая обработка поля, например хеширование пароля. Поля может не быть в сущности |
| `UpdateExisting()` | Обновлять существующие записи. Меняются только поля, указанные в JSON |
| `AllowUnknownProperties()` | Не считать ошибкой поля, которых нет в сущности |
| `ResetPostgresSequences(false)` | Отключить сдвиг счётчиков id в PostgreSQL |
| `LogTo(Console.WriteLine)` | Отчёт: сколько записей добавлено и обновлено по каждой таблице |

Ручной запуск без привязки к миграциям: `await db.SeedFromJsonAsync(seed => seed.AddEmbeddedResources(...));`

## Выгрузка существующих данных из PostgreSQL

Один запрос на любую таблицу — результат можно сразу сохранить как `ИмяТаблицы.json`:

```sql
SELECT jsonb_pretty(jsonb_agg(to_jsonb(t) ORDER BY "Id")) FROM "Products" t;
```

## Ограничения

- По умолчанию изменения в JSON **не применяются** к уже загруженным записям: либо `UpdateExisting()`, либо пересоздание базы.
- id задаются явно — следите, чтобы в разных ветках не появились записи с одинаковым id (это будет обычный конфликт в JSON при слиянии).
- Заменяет собственные `UseSeeding` / `UseAsyncSeeding` в том же контексте: EF Core хранит только одну такую настройку.
- Синхронный запуск (`Migrate()`, `dotnet ef database update`) выполняет асинхронную загрузку с ожиданием результата. Это безопасно в консольных инструментах и ASP.NET Core, но не вызывайте `Migrate()` из UI-потока WPF/WinForms — используйте `MigrateAsync()`.
