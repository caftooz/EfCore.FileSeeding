# EfCore.JsonSeeding

Заполнение базы данных EF Core из JSON, CSV и книг Excel.

- **Один файл — одна таблица** (`Products.json`, `Products.csv`), имя файла — таблица или класс сущности. В книге Excel таблица — это лист.
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

2. Файлы с данными — встроенными ресурсами (тогда они найдутся и при запуске приложения, и в `dotnet ef`):

   ```xml
   <ItemGroup>
     <EmbeddedResource Include="Data\Seed\**\*.json;Data\Seed\**\*.csv;Data\Seed\**\*.xlsx" />
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

## CSV: таблица вместо JSON

Удобно, когда записей много: первая строка — названия столбцов, дальше по строке на запись.
Файл можно вести в Excel или Google Таблицах.

```csv
Id;Name;CategoryId;Price;ImagePath;IsActive
1;Яблоко;1;25,50;image/apple.png;да
2;"Порошок ""Чистота""";3;899;;нет
```

- Разделитель `;`, `,` или табуляция — определяется по первой строке.
- Кодировка UTF-8 или Windows-1251: подходят и «CSV UTF-8», и обычный «CSV (разделители — точка с запятой)» из Excel.
- Числа с запятой или точкой (`25,50`, `1 234,5`); логические значения: `true/false`, `1/0`, `да/нет`.
- Пустая ячейка — значение по умолчанию (как отсутствующее поле в JSON).
- Значение с разделителем, кавычками или переносом строки — в кавычках, кавычка внутри удваивается. Excel делает это сам.
- Даты — `2026-11-10`, перечисления — по имени (`Hidden`) или числом.
- Проверки те же, что для JSON: опечатка в названии столбца — ошибка с номером строки. `Transform` работает и для столбцов (например, `Password`).

## Excel: одна книга, лист = таблица

Положите в папку книгу `.xlsx` (имя файла любое, например `Demo.xlsx`). Каждый лист — одна таблица:
имя листа — имя таблицы (`Products`), первая строка — столбцы, как в CSV.

- Порядок листов не важен: загрузка всё равно идёт по внешним ключам.
- Листы, имя которых начинается с `_` (`_Справка`, `_Черновик`), не загружаются — удобно для заметок.
- Пустые листы («Лист1» по умолчанию) пропускаются.
- Числа, даты и флажки можно вводить обычными ячейками Excel: дата в столбце типа `DateTime`/`DateOnly` читается как дата.
- Формулы допустимы — берётся посчитанное значение. Ячейка с ошибкой (`#Н/Д`) — ошибка загрузки.
- Только `.xlsx`; старый `.xls` нужно пересохранить. Временный файл `~$Demo.xlsx`, который создаёт открытый Excel, игнорируется.

В ошибках указаны книга, лист и строка: «Demo.xlsx, лист «Products», строка 5: …».

В одной папке можно смешивать форматы: `Categories.json`, `Products.csv` и книга Excel.

## Настройки

| Метод | Что делает |
|---|---|
| `AddEmbeddedResources(assembly, folder, enabled)` | Файлы из папки проекта, встроенные в сборку. Источники обрабатываются в порядке добавления |
| `AddDirectory(path, enabled)` | Файлы `.json`, `.csv`, `.xlsx` из папки на диске (относительный путь — от папки приложения) |
| `Transform<T>(field, (entity, value) => ...)` | Особая обработка поля или столбца CSV, например хеширование пароля. Поля может не быть в сущности |
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

Или как CSV (в pgAdmin — Export, в DBeaver — «Экспорт данных» → CSV; в psql):

```sql
\copy (SELECT * FROM "Products" ORDER BY "Id") TO 'Products.csv' WITH (FORMAT csv, HEADER, DELIMITER ';')
```

## Ограничения

- По умолчанию изменения в файлах **не применяются** к уже загруженным записям: либо `UpdateExisting()`, либо пересоздание базы.
- id задаются явно — следите, чтобы в разных ветках не появились записи с одинаковым id (это будет обычный конфликт в файле при слиянии).
- В CSV и Excel пустая ячейка не очищает поле при `UpdateExisting()` — она означает «не указано».
- Заменяет собственные `UseSeeding` / `UseAsyncSeeding` в том же контексте: EF Core хранит только одну такую настройку.
- Синхронный запуск (`Migrate()`, `dotnet ef database update`) выполняет асинхронную загрузку с ожиданием результата. Это безопасно в консольных инструментах и ASP.NET Core, но не вызывайте `Migrate()` из UI-потока WPF/WinForms — используйте `MigrateAsync()`.
