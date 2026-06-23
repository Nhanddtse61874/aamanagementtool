# Worklog (AA Management Tool)

Công cụ chấm công desktop (timesheet) cho team — log giờ theo **Request → Task → ngày**, báo cáo tuần/tháng, cảnh báo "chưa log".

> WPF / .NET 8 · MVVM (CommunityToolkit.Mvvm) · SQLite + Dapper · ClosedXML (export)

## Tính năng

- **Timesheet** — nhập giờ theo tuần, nhóm theo Request (collapsible), tổng theo ngày/tuần/nhóm, Smart fill, cảnh báo > 8h.
- **Requests** — tạo/sửa request + task, áp template task, đếm số task.
- **Reports** — báo cáo tuần (theo ngày) / tháng (theo request·task), drill-down Project→Request→Task→Date, 4 thẻ tổng quan, cảnh báo "chưa log".
- **Users** — quản lý người dùng (avatar, Active/Inactive); tự nhận user theo Windows username.
- **Settings** — đường dẫn DB, ngưỡng cảnh báo "chưa log", quản lý template task.
- Live cross-tab sync, banner cảnh báo bản sao xung đột (OneDrive).

## Chạy

```bash
dotnet build src/TimesheetApp.sln          # build
dotnet test  src/TimesheetApp.sln          # 181 tests
dotnet run --project src/TimesheetApp      # chạy app
```

- App project: `src/TimesheetApp` · Tests: `src/TimesheetApp.Tests`
- DB mặc định: `%USERPROFILE%\Documents\TimesheetApp\timesheet.db`
  (đường dẫn lưu trong `%APPDATA%\TimesheetApp\appsettings.json`)

## Cấu trúc

```
src/TimesheetApp/
  Views/        # XAML: MainWindow (sidebar shell) + Tabs + Theme + Converters
  ViewModels/   # MVVM view-models (CommunityToolkit)
  Services/     # nghiệp vụ: timelog, smart input, default-task sync, export…
  Data/         # repositories + Dapper + khởi tạo schema/migration
  Models/       # entities + read-models
```
