# MediaTagger

Застосунок для тегування медіафайлів з картою, пошуком та нативним drag & drop до будь-якого додатку (Premiere Pro, Photoshop тощо).

## Стек

- **Backend:** .NET 8 ASP.NET Core Web API
- **Frontend:** React 18 + TypeScript + MUI v5 + Leaflet
- **Desktop shell:** Electron 28

---

## Вимоги

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 18+](https://nodejs.org/)
- Windows 10+ або macOS 12+

---

## Встановлення та запуск (розробка)

### 1. Клонуйте репозиторій

```bash
git clone <repo-url>
cd MediaTagger
```

### 2. Встановіть залежності .NET API

```bash
cd MediaTagger.API
dotnet restore
```

### 3. Встановіть залежності React

```bash
cd ../mediaTagger.client
npm install
```

### 4. Встановіть залежності Electron

```bash
cd ../MediaTagger.Electron
npm install
```

---

## Запуск

Відкрийте **3 термінали**:

**Термінал 1 — .NET API:**
```bash
cd MediaTagger.API
dotnet run
# Запускається на http://localhost:5000
```

**Термінал 2 — React:**
```bash
cd mediaTagger.client
npm start
# Запускається на http://localhost:3000
```

**Термінал 3 — Electron:**
```bash
cd MediaTagger.Electron
npm run dev
# Відкриває вікно Electron з React усередині
```

---

## Структура `_tags.json`

Кожна папка містить файл `_tags.json` зі структурою:

```json
{
  "version": 1,
  "files": {
    "IMG_001.jpg": {
      "people": ["Олег Іванченко", "Марія Коваль"],
      "place": "Київ, Майдан Незалежності",
      "groups": ["Сім'я", "Друзі"],
      "albums": ["Літо 2023", "День народження"],
      "coordinates": { "lat": 50.4501, "lng": 30.5234 },
      "notes": "Будь-які нотатки"
    }
  }
}
```

**Примітки:**
- `_tags.json` не відображається у галереї
- `coordinates` — якщо не задані вручну, беруться автоматично з EXIF GPS
- Один файл може належати до кількох альбомів

---

## Drag & Drop до Premiere Pro / Photoshop

1. Наведіть на фото у галереї — з'явиться іконка ☰ у правому верхньому куті
2. Почніть перетягувати фото
3. Electron ініціює **нативний OS drag** з реальним шляхом до файлу
4. Перетягніть у вікно Premiere Pro, Finder, провідник Windows — файл вставиться як посилання

> **Важливо:** це працює лише у Electron-застосунку, не у звичайному браузері.

---

## Мапа

- Вкладка **Мапа** показує всі файли з GPS координатами
- Координати беруться з EXIF (автоматично) або задаються вручну у полі "Координати" бокової панелі
- Формат вводу: `50.4501, 30.5234` (lat, lng)
- Клік на маркер — показує мініатюру, людей, місце, альбоми

---

## Збірка для продакшену

```bash
# Збудуйте React
cd mediaTagger.client
npm run build

# Збудуйте Electron застосунок
cd ../MediaTagger.Electron
npm run build
# Результат у MediaTagger.Electron/dist/
```

---

## API Endpoints

| Метод | URL | Опис |
|-------|-----|------|
| GET | `/api/media/files?folderPath=...` | Список файлів у папці |
| GET | `/api/media/folder-tree?rootPath=...` | Дерево папок |
| PUT | `/api/media/tags` | Оновити теги файлу |
| POST | `/api/media/search` | Пошук по тегах |
| GET | `/api/media/all-tags?rootPath=...` | Всі унікальні теги |
| GET | `/api/media/map-files?rootPath=...` | Файли з координатами |
| GET | `/api/media/thumbnail?filePath=...` | Мініатюра файлу |

## Запуск одною командою

```bash
cd MediaTagger

# Перший раз — встановити всі залежності:
npm install
npm run install:all

# Запустити все:
npm run dev
```

Це запустить паралельно:
- .NET API на `localhost:5001`
- React на `localhost:3000`
- Electron (чекає поки обидва готові, потім відкриває вікно)
