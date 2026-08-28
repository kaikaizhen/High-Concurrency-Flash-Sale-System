# ASP.NET Core 三層式架構開發規範

## 1. 目標

本規範定義 ASP.NET Core Web API 專案的統一三層式架構、資料流、命名方式、資料夾結構與 Dependency Injection 註冊方式。

所有功能皆遵循以下固定依賴方向：

```text
Controller
    ↓
Service
    ↓
Repository
    ↓
Database
```

核心原則：

- `Controller`：處理 HTTP 與 API 邊界。
- `Service`：處理商業邏輯與流程。
- `Repository`：處理資料庫存取。
- `ParamModel`：API 輸入模型。
- `ViewModel`：API 輸出模型。
- `DtoModel`：Controller 與 Service 之間，以及 Service 內部的資料傳遞模型。
- `Entity`：資料庫映射模型。
- `Helper`：無商業流程、無外部 I/O 的純工具。
- `Common`：全域共用定義。
- `Program.cs`：只負責應用程式啟動、Framework 註冊、單一應用程式依賴入口與 Middleware Pipeline。
- 所有應用程式依賴統一由 `AddApplicationDependencies()` 註冊。

---

# 2. 架構總覽

## 2.1 主要分層

```text
HTTP Request
     │
     ▼
┌────────────────────────────┐
│ Controller                 │
│ Presentation Layer         │
└────────────────────────────┘
     │
     │ ParamModel → DtoModel
     ▼
┌────────────────────────────┐
│ Service                    │
│ Business / Application     │
│ Logic Layer                │
└────────────────────────────┘
     │
     │ DtoModel → Entity
     ▼
┌────────────────────────────┐
│ Repository                 │
│ Data Access Layer          │
└────────────────────────────┘
     │
     │ Entity
     ▼
┌────────────────────────────┐
│ Database                   │
└────────────────────────────┘
```

回傳方向：

```text
Database
    │
    ▼
Entity
    │
    ▼
Repository
    │
    ▼
Service
    │
    │ Entity → DtoModel
    ▼
Controller
    │
    │ DtoModel → ViewModel
    ▼
HTTP Response
```

---

# 3. 分層依賴規則

固定依賴方向：

```text
Controller → Service → Repository → Database
```

各層只能依賴下一層提供的抽象：

```text
Controller
    ↓
IService

Service
    ↓
IRepository

Repository
    ↓
DbContext / Dapper / Database
```

各層邊界：

| 層級 | 可處理 | 不跨越的邊界 |
|---|---|---|
| Controller | HTTP、Route、ParamModel、ViewModel、呼叫 Service | 不直接存取資料庫、不直接呼叫 Repository、不撰寫商業流程 |
| Service | 商業規則、流程控制、狀態改變、驗證、Transaction 協調 | 不處理 HTTP、不直接撰寫 SQL、不操作 Controller |
| Repository | Query、CRUD、EF Core、Dapper、Stored Procedure | 不決定商業流程、不呼叫 Service、不處理 HTTP |
| Helper | 純計算、純轉換 | 不存取 DB、不呼叫 API、不處理業務流程 |
| Common | Enum、Constants、Exception、共用結構 | 不放功能流程 |

---

# 4. Controller 規範

## 4.1 職責

Controller 只負責 API 邊界：

1. 接收 HTTP Request。
2. 接收 `ParamModel`。
3. 執行輸入格式層級驗證。
4. 將 `ParamModel` 轉換為 `DtoModel`。
5. 呼叫 `Service`。
6. 將 Service 回傳的 `DtoModel` 轉換為 `ViewModel`。
7. 回傳 HTTP Response。

Controller 不處理商業規則。

## 4.2 標準範例

```csharp
[ApiController]
[Route("api/users")]
public class UserController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly IMapper _mapper;

    public UserController(
        IUserService userService,
        IMapper mapper)
    {
        _userService = userService;
        _mapper = mapper;
    }

    [HttpPost]
    public async Task<ActionResult<UserViewModel>> CreateAsync(
        [FromBody] CreateUserParamModel param)
    {
        var dto = _mapper.Map<CreateUserDtoModel>(param);

        var result = await _userService.CreateAsync(dto);

        var viewModel = _mapper.Map<UserViewModel>(result);

        return Ok(viewModel);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<UserViewModel>> GetByIdAsync(int id)
    {
        var result = await _userService.GetByIdAsync(id);

        var viewModel = _mapper.Map<UserViewModel>(result);

        return Ok(viewModel);
    }
}
```

## 4.3 Controller 邊界

Controller 可使用：

```text
ParamModel
ViewModel
DtoModel
IService
IMapper
HTTP Attribute
Authorization Attribute
Model Binding
```

Controller 不直接使用：

```text
DbContext
Repository
Dapper
SQL
Entity 作為 API Response
商業流程判斷
```

---

# 5. Service 規範

## 5.1 職責

Service 是商業邏輯與應用流程的核心。

Service 負責：

- 商業規則。
- 資料驗證。
- 狀態轉換。
- 流程控制。
- 權限與業務條件判斷。
- 呼叫一個或多個 Repository。
- 協調多個 Service。
- Transaction 流程。
- DTO 與 Entity 的轉換。

例如：

```text
使用者註冊
建立訂單
取消訂單
計算折扣
檢查庫存
狀態核准
付款流程
工作流程推進
```

## 5.2 Service Interface

```csharp
public interface IUserService
{
    Task<UserDtoModel> CreateAsync(CreateUserDtoModel dto);

    Task<UserDtoModel> GetByIdAsync(int id);
}
```

## 5.3 Service 實作

```csharp
public class UserService : IUserService
{
    private readonly IUserRepository _userRepository;
    private readonly IMapper _mapper;

    public UserService(
        IUserRepository userRepository,
        IMapper mapper)
    {
        _userRepository = userRepository;
        _mapper = mapper;
    }

    public async Task<UserDtoModel> CreateAsync(
        CreateUserDtoModel dto)
    {
        var exists = await _userRepository
            .ExistsByEmailAsync(dto.Email);

        if (exists)
        {
            throw new BusinessException("Email already exists.");
        }

        var entity = _mapper.Map<User>(dto);

        entity.CreatedAt = DateTime.UtcNow;
        entity.Status = UserStatus.Active;

        await _userRepository.CreateAsync(entity);

        return _mapper.Map<UserDtoModel>(entity);
    }

    public async Task<UserDtoModel> GetByIdAsync(int id)
    {
        var entity = await _userRepository.GetByIdAsync(id);

        if (entity is null)
        {
            throw new NotFoundException("User not found.");
        }

        return _mapper.Map<UserDtoModel>(entity);
    }
}
```

## 5.4 Service 邊界

Service 可使用：

```text
DtoModel
Entity
IRepository
其他 IService
IMapper
商業 Exception
Transaction
Infrastructure Service Interface
```

Service 不處理：

```text
HttpContext
ActionResult
StatusCode
Route
Controller
直接 SQL
直接 Dapper
直接 DbContext Query
```

---

# 6. Repository 規範

## 6.1 職責

Repository 專責資料存取。

Repository 負責：

- `SELECT`
- `INSERT`
- `UPDATE`
- `DELETE`
- EF Core Query
- Dapper Query
- Stored Procedure
- Database-specific Query Logic

Repository 的輸入與輸出以 `Entity` 為主。

## 6.2 Repository Interface

```csharp
public interface IUserRepository
{
    Task<User?> GetByIdAsync(int id);

    Task<User?> GetByEmailAsync(string email);

    Task<bool> ExistsByEmailAsync(string email);

    Task CreateAsync(User entity);

    Task UpdateAsync(User entity);

    Task DeleteAsync(User entity);
}
```

## 6.3 EF Core Repository

```csharp
public class UserRepository : IUserRepository
{
    private readonly AppDbContext _dbContext;

    public UserRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<User?> GetByIdAsync(int id)
    {
        return await _dbContext.Users
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<User?> GetByEmailAsync(string email)
    {
        return await _dbContext.Users
            .FirstOrDefaultAsync(x => x.Email == email);
    }

    public async Task<bool> ExistsByEmailAsync(string email)
    {
        return await _dbContext.Users
            .AnyAsync(x => x.Email == email);
    }

    public async Task CreateAsync(User entity)
    {
        await _dbContext.Users.AddAsync(entity);
        await _dbContext.SaveChangesAsync();
    }

    public async Task UpdateAsync(User entity)
    {
        _dbContext.Users.Update(entity);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteAsync(User entity)
    {
        _dbContext.Users.Remove(entity);
        await _dbContext.SaveChangesAsync();
    }
}
```

## 6.4 Dapper Repository

```csharp
public class UserQueryRepository : IUserQueryRepository
{
    private readonly IDbConnection _connection;

    public UserQueryRepository(IDbConnection connection)
    {
        _connection = connection;
    }

    public async Task<User?> GetByIdAsync(int id)
    {
        const string sql = """
            SELECT
                Id,
                Name,
                Email,
                Status,
                CreatedAt
            FROM Users
            WHERE Id = @Id
            """;

        return await _connection.QueryFirstOrDefaultAsync<User>(
            sql,
            new { Id = id });
    }
}
```

## 6.5 Repository 邊界

Repository 可使用：

```text
Entity
DbContext
DbSet
EF Core
Dapper
SQL
Stored Procedure
Database Transaction API
```

Repository 不處理：

```text
Controller
HTTP
ViewModel
ParamModel
商業流程
流程狀態決策
其他 Service
```

---

# 7. Model 規範

系統使用以下四種 Model：

```text
ParamModel
DtoModel
Entity
ViewModel
```

## 7.1 ParamModel

用途：

```text
Client → Controller
```

ParamModel 定義 API Request 的輸入格式。

```csharp
public class CreateUserParamModel
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}
```

ParamModel 可包含輸入格式驗證；商業條件驗證放在 Service。

## 7.2 DtoModel

用途：

```text
Controller ↔ Service
Service 內部資料傳遞
```

```csharp
public class CreateUserDtoModel
{
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;
}
```

```csharp
public class UserDtoModel
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public UserStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
}
```

## 7.3 Entity

用途：

```text
Service ↔ Repository ↔ Database
```

```csharp
public class User
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public UserStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
}
```

Entity 不直接作為 API Response。

## 7.4 ViewModel

用途：

```text
Controller → Client
```

```csharp
public class UserViewModel
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
}
```

---

# 8. 固定資料流

新增資料：

```text
HTTP Request
    │
    ▼
ParamModel
    │
    │ AutoMapper
    ▼
DtoModel
    │
    ▼
Service
    │
    │ AutoMapper
    ▼
Entity
    │
    ▼
Repository
    │
    ▼
Database
```

回傳資料：

```text
Database
    │
    ▼
Entity
    │
    ▼
Repository
    │
    ▼
Service
    │
    │ AutoMapper
    ▼
DtoModel
    │
    ▼
Controller
    │
    │ AutoMapper
    ▼
ViewModel
    │
    ▼
HTTP Response
```

---

# 9. AutoMapper 規範

Profile 以 Business Feature 為單位。

```text
Mappings/
├── UserProfile.cs
├── OrderProfile.cs
└── ProductProfile.cs
```

```csharp
public class UserProfile : Profile
{
    public UserProfile()
    {
        CreateMap<CreateUserParamModel, CreateUserDtoModel>();
        CreateMap<CreateUserDtoModel, User>();
        CreateMap<User, UserDtoModel>();

        CreateMap<UserDtoModel, UserViewModel>()
            .ForMember(
                dest => dest.Status,
                opt => opt.MapFrom(src => src.Status.ToString()));
    }
}
```

固定映射方向：

```text
ParamModel → DtoModel
DtoModel   → Entity
Entity     → DtoModel
DtoModel   → ViewModel
```

---

# 10. Helper 規範

Helper 僅處理可重複使用、無商業流程的純工具邏輯。

例如：

```text
DateTimeHelper
StringHelper
HashHelper
ImageResizeHelper
```

Helper 函式應：

- 不存取資料庫。
- 不呼叫外部 API。
- 不改變系統狀態。
- 不控制商業流程。
- 相同輸入得到相同輸出。

```csharp
public static class StringHelper
{
    public static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }
}
```

---

# 11. Common 規範

Common 放置全域共用定義。

```text
Common/
├── Constants/
├── Enums/
├── Exceptions/
└── Results/
```

Enum：

```csharp
public enum UserStatus
{
    Inactive = 0,
    Active = 1,
    Disabled = 2
}
```

Constants：

```csharp
public static class GlobalConstants
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}
```

Exception：

```csharp
public class BusinessException : Exception
{
    public BusinessException(string message)
        : base(message)
    {
    }
}
```

```csharp
public class NotFoundException : Exception
{
    public NotFoundException(string message)
        : base(message)
    {
    }
}
```

---

# 12. Infrastructure Service 規範

具有外部 I/O 的功能以 Service Interface 封裝。

例如：

```text
JWT
Email
Redis
RabbitMQ
File Storage
Azure Blob
AWS S3
第三方 API
```

```text
Infrastructure/
├── Auth/
│   ├── IJwtTokenService.cs
│   └── JwtTokenService.cs
├── Email/
│   ├── IEmailService.cs
│   └── EmailService.cs
└── Storage/
    ├── IFileStorageService.cs
    └── FileStorageService.cs
```

Service 透過 Interface 使用 Infrastructure：

```csharp
public class UserService : IUserService
{
    private readonly IUserRepository _userRepository;
    private readonly IEmailService _emailService;

    public UserService(
        IUserRepository userRepository,
        IEmailService emailService)
    {
        _userRepository = userRepository;
        _emailService = emailService;
    }
}
```

---

# 13. Options Pattern 規範

環境相關設定統一透過：

```text
appsettings.json
        ↓
Options Class
        ↓
IOptions<T>
```

appsettings.json：

```json
{
  "Jwt": {
    "Issuer": "MyApplication",
    "Audience": "MyApplication",
    "ExpireMinutes": 60
  }
}
```

```csharp
public class JwtOptions
{
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int ExpireMinutes { get; set; }
}
```

```csharp
public class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(
        IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }
}
```

環境設定包含：

```text
Connection String
JWT
Redis
RabbitMQ
Email
第三方 API
Storage
```

---

# 14. DbContext 規範

DbContext 放置於：

```text
Data/
├── AppDbContext.cs
└── Configurations/
```

```csharp
public class AppDbContext : DbContext
{
    public AppDbContext(
        DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(AppDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
```

```csharp
public class UserConfiguration :
    IEntityTypeConfiguration<User>
{
    public void Configure(
        EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Email)
            .HasMaxLength(200)
            .IsRequired();
    }
}
```

---

# 15. Dependency Injection 統一規範

所有應用程式自己的 Dependency Injection 統一由：

```csharp
AddApplicationDependencies()
```

管理。

Program.cs 只呼叫這一個入口：

```csharp
builder.Services.AddApplicationDependencies(
    builder.Configuration);
```

---

# 16. DependencyInjectionExtensions.cs

檔案位置：

```text
Extensions/
└── DependencyInjectionExtensions.cs
```

完整範例：

```csharp
public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddApplicationDependencies(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        RegisterServices(services);
        RegisterRepositories(services);
        RegisterDatabase(services, configuration);
        RegisterMappings(services);
        RegisterOptions(services, configuration);
        RegisterInfrastructureServices(services);

        return services;
    }

    private static void RegisterServices(
        IServiceCollection services)
    {
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IProductService, ProductService>();
    }

    private static void RegisterRepositories(
        IServiceCollection services)
    {
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
    }

    private static void RegisterDatabase(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseSqlServer(
                configuration.GetConnectionString(
                    "DefaultConnection"));
        });
    }

    private static void RegisterMappings(
        IServiceCollection services)
    {
        services.AddAutoMapper(
            typeof(UserProfile).Assembly);
    }

    private static void RegisterOptions(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<JwtOptions>(
            configuration.GetSection("Jwt"));
    }

    private static void RegisterInfrastructureServices(
        IServiceCollection services)
    {
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<IFileStorageService, FileStorageService>();
    }
}
```

DI 結構：

```text
Program.cs
    │
    ▼
AddApplicationDependencies(configuration)
    │
    ├── RegisterServices()
    ├── RegisterRepositories()
    ├── RegisterDatabase()
    ├── RegisterMappings()
    ├── RegisterOptions()
    └── RegisterInfrastructureServices()
```

---

# 17. Program.cs 標準規範

Program.cs 保持簡潔，只負責：

1. 建立 `WebApplicationBuilder`。
2. Framework Service 註冊。
3. 呼叫 `AddApplicationDependencies()`。
4. 建立 Application。
5. Middleware Pipeline。
6. Map Controllers。
7. Run。

完整範例：

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddApplicationDependencies(
    builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
```

應用程式自有的 Service、Repository、DbContext、AutoMapper、Options、Infrastructure Service 全部由：

```csharp
builder.Services.AddApplicationDependencies(
    builder.Configuration);
```

統一註冊。

---

# 18. Exception 處理規範

商業錯誤由 Service 丟出對應 Exception。

```csharp
if (exists)
{
    throw new BusinessException(
        "Email already exists.");
}
```

```csharp
if (entity is null)
{
    throw new NotFoundException(
        "User not found.");
}
```

API 統一使用 Global Exception Handler 轉換成 HTTP Response。

```text
Service
    │
    │ throw Exception
    ▼
Global Exception Handler
    │
    ▼
HTTP Status Code + Error Response
```

Controller 不負責重複撰寫商業 Exception 的 try/catch。

---

# 19. Transaction 規範

Transaction 的流程邊界由 Service 決定。

當一個商業操作包含多個 Repository 寫入：

```text
Service
    │
    ├── Repository A
    ├── Repository B
    └── Repository C
```

Service 負責確保這些操作屬於同一個商業交易。

例如：

```text
建立訂單
    │
    ├── 建立 Order
    ├── 建立 OrderItem
    ├── 更新 Stock
    └── 建立 PaymentRecord
```

整體成功才 Commit，任一步驟失敗則 Rollback。

---

# 20. 命名規範

| 類型 | 命名 |
|---|---|
| Controller | `UserController` |
| Service | `UserService` |
| Service Interface | `IUserService` |
| Repository | `UserRepository` |
| Repository Interface | `IUserRepository` |
| Entity | `User` |
| ParamModel | `CreateUserParamModel` |
| DtoModel | `CreateUserDtoModel` |
| ViewModel | `UserViewModel` |
| Mapping Profile | `UserProfile` |
| DbContext | `AppDbContext` |
| Entity Configuration | `UserConfiguration` |
| Helper | `StringHelper` |
| Options | `JwtOptions` |
| Exception | `BusinessException` |
| Infrastructure Service | `JwtTokenService` |

---

# 21. 方法命名規範

非同步方法統一使用 `Async`：

```text
CreateAsync
UpdateAsync
DeleteAsync
GetByIdAsync
GetByEmailAsync
ExistsByEmailAsync
GetListAsync
```

Query 方法應直接表達查詢條件：

```csharp
GetByIdAsync(int id)
GetByEmailAsync(string email)
ExistsByEmailAsync(string email)
GetActiveUsersAsync()
```

Command 方法應直接表達動作：

```csharp
CreateAsync(User entity)
UpdateAsync(User entity)
DeleteAsync(User entity)
```

---

# 22. Model 資料夾規範

Model 預設先依模型類型分類：

```text
Models/
├── Entities/
├── Dtos/
├── Params/
└── ViewModels/
```

各資料夾責任：

```text
Models/Entities/
    = Database Entity

Models/Dtos/
    = Controller 與 Service 之間，以及 Service 內部的資料傳遞模型

Models/Params/
    = API Request Input

Models/ViewModels/
    = API Response Output
```

## 22.1 一般專案

當 Model 數量仍少、檔案容易查找時，直接放在模型類型資料夾中：

```text
Models/
├── Entities/
│   ├── User.cs
│   ├── Order.cs
│   └── Product.cs
│
├── Dtos/
│   ├── CreateUserDtoModel.cs
│   ├── UpdateUserDtoModel.cs
│   ├── UserDtoModel.cs
│   ├── CreateOrderDtoModel.cs
│   └── OrderDtoModel.cs
│
├── Params/
│   ├── CreateUserParamModel.cs
│   ├── UpdateUserParamModel.cs
│   ├── CreateOrderParamModel.cs
│   └── UpdateOrderParamModel.cs
│
└── ViewModels/
    ├── UserViewModel.cs
    ├── UserDetailViewModel.cs
    ├── OrderViewModel.cs
    └── ProductViewModel.cs
```

## 22.2 大型專案

當單一模型類型資料夾中的檔案過多時，在該模型類型底下再依 Business Feature 分組。

固定結構：

```text
Models/
├── Entities/
│   ├── User.cs
│   ├── Order.cs
│   └── Product.cs
│
├── Dtos/
│   ├── Users/
│   ├── Orders/
│   └── Products/
│
├── Params/
│   ├── Users/
│   ├── Orders/
│   └── Products/
│
└── ViewModels/
    ├── Users/
    ├── Orders/
    └── Products/
```

範例：

```text
Models/
├── Entities/
│   ├── User.cs
│   ├── Order.cs
│   └── Product.cs
│
├── Dtos/
│   ├── Users/
│   │   ├── CreateUserDtoModel.cs
│   │   ├── UpdateUserDtoModel.cs
│   │   └── UserDtoModel.cs
│   │
│   ├── Orders/
│   │   ├── CreateOrderDtoModel.cs
│   │   └── OrderDtoModel.cs
│   │
│   └── Products/
│       ├── CreateProductDtoModel.cs
│       └── ProductDtoModel.cs
│
├── Params/
│   ├── Users/
│   │   ├── CreateUserParamModel.cs
│   │   └── UpdateUserParamModel.cs
│   │
│   ├── Orders/
│   │   ├── CreateOrderParamModel.cs
│   │   └── UpdateOrderParamModel.cs
│   │
│   └── Products/
│       └── CreateProductParamModel.cs
│
└── ViewModels/
    ├── Users/
    │   ├── UserViewModel.cs
    │   └── UserDetailViewModel.cs
    │
    ├── Orders/
    │   └── OrderViewModel.cs
    │
    └── Products/
        └── ProductViewModel.cs
```

大型專案的固定原則：

```text
先依 Model Type 分類
    ↓
再依 Business Feature 分組
```

例如：

```text
Models/Params/Users/CreateUserParamModel.cs
Models/Dtos/Users/CreateUserDtoModel.cs
Models/ViewModels/Users/UserViewModel.cs
```

`Entity` 仍統一放置於：

```text
Models/Entities/
```

Entity 不再依 Feature 建立第二層資料夾。

---

# 23. 完整專案資料夾結構

```text
MyProject/
│
├── Controllers/
│   ├── UserController.cs
│   ├── OrderController.cs
│   └── ProductController.cs
│
├── Services/
│   ├── Interfaces/
│   │   ├── IUserService.cs
│   │   ├── IOrderService.cs
│   │   └── IProductService.cs
│   ├── UserService.cs
│   ├── OrderService.cs
│   └── ProductService.cs
│
├── Repositories/
│   ├── Interfaces/
│   │   ├── IUserRepository.cs
│   │   ├── IOrderRepository.cs
│   │   └── IProductRepository.cs
│   ├── UserRepository.cs
│   ├── OrderRepository.cs
│   └── ProductRepository.cs
│
├── Models/
│   ├── Entities/
│   │   ├── User.cs
│   │   ├── Order.cs
│   │   └── Product.cs
│   │
│   ├── Dtos/
│   │   ├── Users/
│   │   │   ├── CreateUserDtoModel.cs
│   │   │   ├── UpdateUserDtoModel.cs
│   │   │   └── UserDtoModel.cs
│   │   ├── Orders/
│   │   │   ├── CreateOrderDtoModel.cs
│   │   │   └── OrderDtoModel.cs
│   │   └── Products/
│   │       ├── CreateProductDtoModel.cs
│   │       └── ProductDtoModel.cs
│   │
│   ├── Params/
│   │   ├── Users/
│   │   │   ├── CreateUserParamModel.cs
│   │   │   └── UpdateUserParamModel.cs
│   │   ├── Orders/
│   │   │   ├── CreateOrderParamModel.cs
│   │   │   └── UpdateOrderParamModel.cs
│   │   └── Products/
│   │       └── CreateProductParamModel.cs
│   │
│   └── ViewModels/
│       ├── Users/
│       │   ├── UserViewModel.cs
│       │   └── UserDetailViewModel.cs
│       ├── Orders/
│       │   └── OrderViewModel.cs
│       └── Products/
│           └── ProductViewModel.cs
│
├── Mappings/
│   ├── UserProfile.cs
│   ├── OrderProfile.cs
│   └── ProductProfile.cs
│
├── Data/
│   ├── AppDbContext.cs
│   └── Configurations/
│       ├── UserConfiguration.cs
│       ├── OrderConfiguration.cs
│       └── ProductConfiguration.cs
│
├── Infrastructure/
│   ├── Auth/
│   │   ├── IJwtTokenService.cs
│   │   └── JwtTokenService.cs
│   ├── Email/
│   │   ├── IEmailService.cs
│   │   └── EmailService.cs
│   └── Storage/
│       ├── IFileStorageService.cs
│       └── FileStorageService.cs
│
├── Helpers/
│   ├── StringHelper.cs
│   ├── DateTimeHelper.cs
│   └── HashHelper.cs
│
├── Common/
│   ├── Constants/
│   │   └── GlobalConstants.cs
│   ├── Enums/
│   │   └── UserStatus.cs
│   ├── Exceptions/
│   │   ├── BusinessException.cs
│   │   └── NotFoundException.cs
│   └── Results/
│
├── Options/
│   └── JwtOptions.cs
│
├── Extensions/
│   └── DependencyInjectionExtensions.cs
│
├── Middlewares/
│   └── GlobalExceptionMiddleware.cs
│
├── Program.cs
├── appsettings.json
└── MyProject.csproj
```

---

# 24. 新增 Business Feature 標準流程

假設新增：

```text
Product
```

固定步驟：

```text
1. 建立 Entity
       ↓
2. 建立 ParamModel
       ↓
3. 建立 DtoModel
       ↓
4. 建立 ViewModel
       ↓
5. 建立 Repository Interface
       ↓
6. 建立 Repository
       ↓
7. 建立 Service Interface
       ↓
8. 建立 Service
       ↓
9. 建立 AutoMapper Profile
       ↓
10. 建立 Controller
       ↓
11. 在 AddApplicationDependencies() 註冊依賴
       ↓
12. 建立或更新測試
```

需要建立的檔案：

```text
Models/Entities/Product.cs

Models/Params/Products/CreateProductParamModel.cs

Models/Dtos/Products/CreateProductDtoModel.cs
Models/Dtos/Products/ProductDtoModel.cs

Models/ViewModels/Products/ProductViewModel.cs

Repositories/Interfaces/IProductRepository.cs
Repositories/ProductRepository.cs

Services/Interfaces/IProductService.cs
Services/ProductService.cs

Mappings/ProductProfile.cs

Controllers/ProductController.cs
```

Dependency Injection：

```csharp
private static void RegisterServices(
    IServiceCollection services)
{
    services.AddScoped<IProductService, ProductService>();
}
```

```csharp
private static void RegisterRepositories(
    IServiceCollection services)
{
    services.AddScoped<IProductRepository, ProductRepository>();
}
```

Program.cs 維持：

```csharp
builder.Services.AddApplicationDependencies(
    builder.Configuration);
```

---

# 25. 完整 User 建立流程範例

Request：

```http
POST /api/users
Content-Type: application/json
```

```json
{
  "name": "Kai",
  "email": "kai@example.com"
}
```

完整流程：

```text
POST /api/users
       │
       ▼
┌──────────────────────────┐
│ CreateUserParamModel     │
└──────────────────────────┘
       │
       │ AutoMapper
       ▼
┌──────────────────────────┐
│ CreateUserDtoModel       │
└──────────────────────────┘
       │
       ▼
┌──────────────────────────┐
│ UserService              │
│                          │
│ 1. 檢查 Email            │
│ 2. 套用商業規則          │
│ 3. 建立 Entity           │
└──────────────────────────┘
       │
       ▼
┌──────────────────────────┐
│ UserRepository           │
│                          │
│ EF Core / Dapper         │
└──────────────────────────┘
       │
       ▼
┌──────────────────────────┐
│ Database                 │
└──────────────────────────┘
       │
       ▼
      User
     Entity
       │
       ▼
 UserDtoModel
       │
       ▼
 UserViewModel
       │
       ▼
HTTP Response
```

---

# 26. 功能放置判斷規則

## HTTP / API 問題

例如：

```text
Route
Header
Query String
Request
Response
Status Code
Model Binding
```

位置：

```text
Controller
```

## 商業規則

例如：

```text
使用者是否可以註冊
訂單是否可以取消
會員折扣是多少
庫存是否足夠
狀態能否進入下一階段
操作是否符合業務條件
```

位置：

```text
Service
```

## Database 操作

例如：

```text
SELECT
INSERT
UPDATE
DELETE
EF Core
Dapper
Stored Procedure
```

位置：

```text
Repository
```

## 純工具邏輯

例如：

```text
字串正規化
日期格式轉換
Hash 計算
單純數值轉換
```

位置：

```text
Helper
```

## 外部系統 I/O

例如：

```text
Email
JWT
Redis
RabbitMQ
File Storage
第三方 API
```

位置：

```text
Infrastructure Service
```

---

# 27. AI 實作規則

AI 在此專案中新增或修改程式碼時，必須遵守以下規則。

## Rule 1：固定依賴方向

```text
Controller
    ↓
Service
    ↓
Repository
    ↓
Database
```

不得跨層呼叫。

## Rule 2：Controller 固定責任

```text
HTTP Request
ParamModel
Mapping
Service Invocation
ViewModel
HTTP Response
```

## Rule 3：Service 固定責任

```text
Business Logic
Application Flow
Validation
State Transition
Repository Coordination
Transaction Coordination
```

## Rule 4：Repository 固定責任

```text
Query
CRUD
EF Core
Dapper
SQL
Stored Procedure
```

## Rule 5：輸入模型固定流程

```text
ParamModel
    ↓
DtoModel
    ↓
Service
```

## Rule 6：資料庫模型固定流程

```text
Service
    ↓
Entity
    ↓
Repository
    ↓
Database
```

## Rule 7：回傳模型固定流程

```text
Database
    ↓
Entity
    ↓
Service
    ↓
DtoModel
    ↓
Controller
    ↓
ViewModel
```

## Rule 8：Dependency Injection 固定入口

所有應用程式依賴統一加入：

```text
DependencyInjectionExtensions.cs
```

並由：

```csharp
AddApplicationDependencies()
```

管理。

Program.cs 固定呼叫：

```csharp
builder.Services.AddApplicationDependencies(
    builder.Configuration);
```

## Rule 9：新增功能的依賴統一註冊

新增：

```text
Service
Repository
DbContext
AutoMapper
Options
Infrastructure Service
```

時，一律更新：

```text
DependencyInjectionExtensions.cs
```

Program.cs 不隨 Business Feature 增加而改變。

## Rule 10：Model 邊界固定

API Request：

```text
ParamModel
```

API Response：

```text
ViewModel
```

Database：

```text
Entity
```

內部傳遞：

```text
DtoModel
```

## Rule 11：Entity 回傳流程固定

```text
Entity
    ↓
DtoModel
    ↓
ViewModel
```

## Rule 12：商業 Exception 由 Service 產生

例如：

```text
BusinessException
NotFoundException
ValidationException
ConflictException
```

由統一 Exception Handler 轉換為 HTTP Response。


## Rule 13：大型專案的 Model 依 Feature 再分組

Model 的第一層固定依用途分類：

```text
Models/
├── Entities/
├── Dtos/
├── Params/
└── ViewModels/
```

當 `Dtos`、`Params`、`ViewModels` 中檔案數量過多時，再依 Business Feature 建立第二層資料夾：

```text
Models/Params/Users/
Models/Dtos/Users/
Models/ViewModels/Users/
```

例如：

```text
Models/Params/Users/CreateUserParamModel.cs
Models/Dtos/Users/CreateUserDtoModel.cs
Models/ViewModels/Users/UserViewModel.cs
```

固定原則：

```text
Model Type
    ↓
Business Feature
    ↓
Model File
```

`Entities` 統一維持：

```text
Models/Entities/
```

---

# 28. 開發前檢查表

- [ ] 是否建立正確的 `ParamModel`。
- [ ] 是否建立正確的 `DtoModel`。
- [ ] 是否建立正確的 `ViewModel`。
- [ ] 是否需要新增或調整 `Entity`。
- [ ] Database 存取是否全部位於 `Repository`。
- [ ] 商業邏輯是否全部位於 `Service`。
- [ ] Controller 是否只處理 API 邊界。
- [ ] Mapping 是否加入對應 `Profile`。
- [ ] Service Interface 是否建立。
- [ ] Repository Interface 是否建立。
- [ ] DI 是否加入 `AddApplicationDependencies()`。
- [ ] Program.cs 是否保持固定結構。
- [ ] 是否需要 Options。
- [ ] 是否需要 Infrastructure Service。
- [ ] 是否建立對應測試。

---

# 29. 架構最終摘要

```text
                         Client
                           │
                           ▼
                    HTTP Request
                           │
                           ▼
                ┌────────────────────┐
                │ Controller         │
                │ Presentation Layer │
                └────────────────────┘
                           │
                    Param → DTO
                           │
                           ▼
                ┌────────────────────┐
                │ Service            │
                │ Business Logic     │
                └────────────────────┘
                           │
                    DTO → Entity
                           │
                           ▼
                ┌────────────────────┐
                │ Repository         │
                │ Data Access        │
                └────────────────────┘
                           │
                           ▼
                       Database
```

Dependency Injection：

```text
Program.cs
    │
    ▼
builder.Services.AddApplicationDependencies(
    builder.Configuration)
    │
    ▼
DependencyInjectionExtensions
    │
    ├── RegisterServices()
    ├── RegisterRepositories()
    ├── RegisterDatabase()
    ├── RegisterMappings()
    ├── RegisterOptions()
    └── RegisterInfrastructureServices()
```

最終責任分工：

```text
Controller
    = HTTP / API Boundary

Service
    = Business Logic / Application Flow

Repository
    = Database Access

ParamModel
    = API Input

DtoModel
    = Internal Data Transfer

Entity
    = Database Model

ViewModel
    = API Output

Helper
    = Pure Utility

Common
    = Shared Definitions

Infrastructure
    = External I/O

DependencyInjectionExtensions
    = Dependency Composition

Program.cs
    = Application Startup
```

本專案所有功能皆依照此規範實作。
