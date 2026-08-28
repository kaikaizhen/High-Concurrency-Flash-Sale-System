# 前端架構規範：清楚的責任邊界

本文件用於統一前端專案的架構、責任邊界、資料夾配置與命名方式。

適用範圍：

- Vue 3 + TypeScript
- React + TypeScript

本文件不要求所有前端專案使用完全相同的技術套件，而是統一以下核心原則：

- 哪一類程式碼應放在哪裡
- 哪些責任不應互相混雜
- Vue 與 React 對應概念如何落地
- 小型與中大型專案如何拆分
- 何時應共享，何時應保留在功能內部

---

# 一、核心架構原則

前端主要可依責任區分為：

```text
Page / View
    ↓
Component
    ↓
Composable / Hook（需要時）
    ↓
Store（需要共享狀態時）
    ↓
API Module
    ↓
HTTP Client
```

實際功能不要求一定經過所有層。

例如單純 UI 互動：

```text
Component
    ↓
Local State
```

例如頁面取得資料：

```text
Page / View
    ↓
API Module
```

例如需要重複使用 loading、error、query 與狀態管理的功能：

```text
Page / View
    ↓
Composable / Hook
    ↓
API Module
```

例如多個頁面共享登入狀態：

```text
Page / View
    ↓
Composable / Hook
    ↓
Store
```

因此本規範的核心不是：

> 每個功能都必須經過固定層數。

而是：

> 每個模組都應有明確責任，沒有實際責任的層不需要建立。

---

# 二、前端主要角色定義

## 1. Page / View

Page / View 代表一個完整頁面。

### Vue

通常命名為：

```text
UserListView.vue
LoginView.vue
DashboardView.vue
```

`View` 為 Vue 專案常見命名方式。

### React

通常命名為：

```text
UserListPage.tsx
LoginPage.tsx
DashboardPage.tsx
```

`Page` 為 React 專案常見命名方式。

### 主要責任

Page / View 負責：

- 對應一個 Route
- 組合多個 Component
- 讀取 Route Parameter
- 讀取 Query Parameter
- 管理頁面層級 State
- 管理搜尋、排序、篩選、分頁
- 管理頁面 loading、error、empty state
- 決定頁面層級的導向行為
- 呼叫 API Module
- 或透過 Composable / Hook 執行頁面流程

### 判斷原則

如果一段邏輯回答的是：

> 「這個頁面目前要做什麼？」

通常屬於 Page / View。

例如：

- 頁面載入後取得資料
- 搜尋條件改變後重新查詢
- 刪除完成後重新整理列表
- Route Parameter 改變後重新載入內容

### 不應負責

Page / View 不應：

- 放入大量可重複 UI 細節
- 重複實作相同 reactive 邏輯
- 重複建立 HTTP Client 設定
- 塞入所有子元件細節
- 因為是頁面入口就承擔所有功能

---

# 三、Component

Component 是可組合的 UI 單元。

例如：

```text
UserCard
SearchBar
Pagination
MaintenanceDialog
SkillNode
PermissionButton
```

### 主要責任

Component 負責：

- UI 顯示
- 接收外部資料
- 對外發送事件
- 管理 Local State
- 處理局部互動
- 處理自身 UI 驗證
- 封裝與自身畫面高度相關的格式化行為

---

## 共用 Component

共用 Component 應盡量：

- 不知道特定 Route
- 不知道特定 API Endpoint
- 不依賴某一個特定功能模組
- 透過 props / event / callback 接收外部行為

例如：

```text
Button
Dialog
DataTable
Pagination
DatePicker
```

---

## 業務 Component

業務 Component 是只服務某個功能的 UI 元件。

例如：

```text
OrderStatusPanel
MachineMaintenanceDialog
UserPermissionEditor
```

這類 Component 可以：

- 讀取該功能需要的 Store
- 使用該功能 Composable / Hook
- 在功能單純時直接使用 API Module

但如果內部逐漸出現：

- 多個 API 呼叫
- loading
- error
- watch / effect
- retry
- 分頁
- 複雜 reactive state

應優先將這些邏輯抽到 Composable / Hook。

---

# 四、Page / View 與 Component 的邊界

最常見的問題是：

> 什麼應該放 Page / View，什麼應該放 Component？

判斷方式：

### 放 Page / View

如果邏輯屬於：

- 整個頁面的載入
- Route 狀態
- Query 狀態
- 頁面級搜尋
- 頁面級分頁
- 頁面級導向
- 多個 Component 之間的協調

通常放 Page / View。

### 放 Component

如果邏輯屬於：

- 某個 Dialog 是否展開
- Input 值
- Dropdown 是否打開
- Table Row 點擊
- 某個區塊的 UI 顯示
- 可重複的 UI 操作

通常放 Component。

核心判斷：

> Page / View 管整個畫面流程；Component 管自己的 UI 責任。

---

# 五、Composable / Hook

Composable 與 Hook 都用來封裝：

> 可重複使用的框架狀態與副作用邏輯。

兩者概念相近，但屬於不同框架。

---

## Vue Composable

`（Vue 專案使用）`

Composable 常使用：

- ref
- reactive
- computed
- watch
- watchEffect
- onMounted
- onUnmounted

命名通常為：

```text
usePermission.ts
usePagination.ts
useListPageState.ts
useTransferLazyLoad.ts
```

---

## React Hook

`（React 專案使用）`

Hook 常使用：

- useState
- useMemo
- useEffect
- useCallback
- useRef

命名通常為：

```text
usePermission.ts
usePagination.ts
useListPageState.ts
useTransferLazyLoad.ts
```

---

## 適合放入 Composable / Hook

以下邏輯適合抽出：

- 可重複的查詢流程
- 分頁
- 篩選
- debounce
- permission
- loading / error
- API 呼叫與 reactive state 組合
- DOM cleanup
- resize / scroll 監聽
- reusable form behavior
- 多個 Component 共用的互動流程

---

## 不適合放入 Composable / Hook

以下內容通常不需要：

- 單純日期格式化
- 單純字串處理
- 單純數值計算
- 無狀態資料轉換

這些應放 Utils。

---

# 六、Composable / Hook 與 Component 的邊界

如果一段邏輯回答：

> 「這個 UI 要顯示什麼？」

通常屬於 Component。

如果回答：

> 「這個 reactive 行為如何重複使用？」

通常屬於 Composable / Hook。

例如：

Component 負責：

```text
是否顯示 loading spinner
```

Composable / Hook 負責：

```text
目前 loading 狀態是什麼
何時切換 loading
何時重新取得資料
```

---

# 七、Store

Store 用於：

> 多個獨立頁面或元件都需要讀寫的共享狀態。

不是所有 State 都應放 Store。

---

## 適合放入 Store

例如：

- Current User
- Authentication State
- Permission
- Theme
- Language
- Shopping Cart
- 多頁共用設定
- 跨頁需要保留的狀態

---

## 不適合放入 Store

例如：

- 單一 Dialog 是否開啟
- 單一 View 的搜尋關鍵字
- 某個表單尚未送出的輸入值
- 單一 Component 的 Dropdown State
- 只為避免 props / event 而存在的短期狀態

---

# 八、Vue Store

`（Vue 專案使用）`

Vue 專案建議使用 Pinia。

命名例如：

```text
useMainStore
useAuthStore
usePermissionStore
useCartStore
```

資料夾：

```text
src/stores/
```

或以 Feature 拆分：

```text
src/features/auth/stores/
src/features/cart/stores/
```

Store 可包含：

- state
- getter
- action

Store action 可以使用 API Module，但 Store 的核心仍應是：

> 共享狀態。

不應因為 Pinia action 可以寫流程，就把所有頁面流程全部放進 Store。

---

# 九、React Store

`（React 專案使用）`

React 並沒有官方唯一 Store 方案。

可依專案既有選擇使用：

- Context
- Zustand
- Redux Toolkit
- 其他狀態管理工具

資料夾可統一為：

```text
src/stores/
```

或：

```text
src/features/auth/stores/
```

命名例如：

```text
useAuthStore.ts
usePermissionStore.ts
useCartStore.ts
```

若使用 Redux，可依 Redux 慣例使用：

```text
authSlice.ts
cartSlice.ts
```

無論採用何種 library，責任相同：

> Store 用來管理真正需要共享的狀態，不應成為所有程式邏輯的集中地。

---

# 十、Store 與 Composable / Hook 的邊界

這兩者很容易混淆。

## Store

回答：

> 「共享資料目前是什麼？」

例如：

```text
currentUser
permissions
theme
cart
```

## Composable / Hook

回答：

> 「某段 reactive 行為如何運作？」

例如：

```text
如何載入列表
如何處理 loading
如何處理 debounce
如何控制 lazy load
如何監聽 resize
```

如果某段邏輯只在一個頁面使用，而且不需要共享：

> 優先留在 Page 或 Composable / Hook。

如果多個獨立頁面需要共享同一份狀態：

> 才考慮 Store。

---

# 十一、API Module

API Module 負責定義前端的資料請求入口。

例如：

```text
userApi.ts
machineApi.ts
workInProcessApi.ts
permissionApi.ts
```

### 主要責任

API Module 負責：

- request path
- HTTP method
- query parameter
- path parameter
- request payload
- response type
- 呼叫共用 HTTP Client
- 按功能領域整理資料請求

---

## API Module 不負責

API Module 不應：

- 控制 Dialog
- 顯示 Toast
- 控制頁面 loading
- 操作 Component State
- 決定 Route
- 持有 reactive state
- 操作 DOM

API Module 應專注：

> 資料請求本身。

---

# 十二、HTTP Client

HTTP Client 是所有 API Module 共用的底層請求工具。

例如：

```text
http.ts
httpClient.ts
request.ts
```

### 主要責任

HTTP Client 統一處理：

- base URL
- default header
- credentials
- timeout
- interceptor
- 通用 request 設定
- 通用 response 設定
- 通用 network error

---

## HTTP Client 與 API Module 的邊界

HTTP Client 不應知道具體功能名稱。

例如 HTTP Client 不應知道：

```text
User
Order
Machine
Report
```

API Module 才知道具體資料請求。

關係：

```text
HTTP Client
   ↑
UserApi
OrderApi
MachineApi
ReportApi
```

---

# 十三、錯誤處理邊界

錯誤處理應依層級拆分。

## HTTP Client

負責共通技術性問題：

- timeout
- network error
- request interceptor
- response interceptor
- 通用狀態碼處理

---

## API Module

保留該 request 的錯誤資訊與 response 結構。

API Module 不應直接：

- Toast
- Dialog
- Alert
- 操作畫面

---

## Composable / Hook

可以處理與功能流程相關的：

- loading
- error state
- retry
- error mapping
- reusable request state

---

## Page / View / Component

負責最後的 UI 呈現：

- 顯示 Toast
- 顯示 Error Message
- 顯示 Error Page
- 開啟 Dialog
- 顯示 Retry Button

核心原則：

> 越底層越描述「發生了什麼」；越接近 UI 越決定「如何呈現」。

---

# 十四、Utils

Utils 用於：

> 無狀態、可預期、可獨立測試的通用函式。

例如：

```text
formatDate
formatCurrency
normalizeString
buildQueryString
parseNumber
```

### Utils 應盡量具備

- 沒有 reactive state
- 不依賴 Component
- 不依賴 Router
- 不依賴 Store
- 不發送 request
- 相同輸入得到可預期結果

### Utils 不應

- 呼叫 API
- 修改 Store
- 操作 Router
- 開 Dialog
- 顯示 Toast
- 持有 UI State

---

# 十五、Directive

`（Vue 專案使用）`

Directive 用於封裝可重複的 DOM 行為。

例如：

```text
permission.ts
scanInput.ts
clickOutside.ts
focus.ts
```

適合：

- 權限顯示控制
- focus
- click outside
- scan input
- DOM behavior

不適合：

- 頁面資料載入
- API 流程
- Store 流程
- 頁面 Navigation

---

# 十六、React 對應 DOM 邏輯

React 沒有 Vue Directive 的直接等價資料夾。

類似需求通常使用：

- Custom Hook
- Component
- ref
- event handler

因此 React 專案通常不需要：

```text
directives/
```

如果某段 DOM 行為需要重複使用：

> 優先考慮 Custom Hook 或專用 Component。

---

# 十七、Types

TypeScript 型別應依使用範圍放置。

---

## API 專用 Type

如果型別只服務單一 API Module，可與該 API 放在同一功能區域。

例如：

```text
api/
├── userApi.ts
└── user.types.ts
```

或：

```text
features/users/
├── api/
│   ├── userApi.ts
│   └── user.types.ts
```

---

## Feature Type

只服務某一個功能的型別：

```text
features/users/types/
features/orders/types/
```

---

## Shared Type

只有跨多個 Feature 共用時，才放：

```text
src/types/
```

或：

```text
src/shared/types/
```

---

# 十八、API Data 與 UI Model

資料請求取得的結構，不一定適合直接在整個 UI 中使用。

如果資料格式與 UI 需求不同，應在靠近功能邊界的位置轉換。

適合的位置：

- Composable
- Hook
- Feature mapper
- View 邊界

不建議讓原始欄位格式一路散佈到多層 Component。

如果資料完全相同，則不需要為了形式額外建立 Mapper。

核心原則：

> 有實際格式差異才轉換，不為抽象而抽象。

---

# 十九、Router

Router 負責：

- URL 與 Page / View 對應
- Navigation
- Redirect
- Route Parameter
- Query Parameter
- Route Guard

---

## Vue Router

`（Vue 專案使用）`

資料夾：

```text
src/router/
```

可包含：

```text
index.ts
routes.ts
guards.ts
```

---

## React Router

`（React 專案使用）`

資料夾可使用：

```text
src/router/
```

或：

```text
src/routes/
```

可包含：

```text
routes.tsx
guards/
layouts/
```

---

## Router 不應

Router 不應承擔：

- 表單流程
- UI 資料載入
- 大量狀態管理
- 特定頁面主要流程

Router 的責任是：

> 導航與路由邊界。

---

# 二十、權限

權限可分為：

## 路由權限

負責：

> 是否允許進入某個頁面。

放在：

- Router Guard
- Route Wrapper
- Route Config

---

## UI 權限

負責：

> 是否顯示或允許操作某個 UI。

例如：

- Button 是否顯示
- Button 是否 disabled
- Menu 是否顯示

可透過：

- Permission Composable
- Permission Hook
- Permission Store
- Permission Directive `（Vue）`
- Permission Component `（React）`

---

# 二十一、Vue 專案推薦資料夾結構

適用：

- Vue 3
- TypeScript
- Vue Router
- Pinia

```text
src/
├── api/
│   ├── http.ts
│   ├── userApi.ts
│   └── machineApi.ts
│
├── assets/
│   ├── images/
│   ├── icons/
│   └── styles/
│
├── components/
│   ├── common/
│   └── business/
│
├── composables/
│   ├── usePermission.ts
│   ├── usePagination.ts
│   └── useListPageState.ts
│
├── directives/
│   ├── permission.ts
│   └── scanInput.ts
│
├── router/
│   ├── index.ts
│   ├── routes.ts
│   └── guards.ts
│
├── stores/
│   ├── useMainStore.ts
│   ├── useAuthStore.ts
│   └── usePermissionStore.ts
│
├── types/
│   └── global.types.ts
│
├── utils/
│   ├── formatDate.ts
│   └── formatNumber.ts
│
├── views/
│   ├── LoginView.vue
│   ├── DashboardView.vue
│   └── users/
│       └── UserListView.vue
│
├── plugins/
│   └── index.ts
│
├── App.vue
└── main.ts
```

---

# 二十二、Vue 中大型專案推薦結構

當功能逐漸增加，優先以 Feature 分組。

```text
src/
├── features/
│   ├── auth/
│   │   ├── api/
│   │   ├── components/
│   │   ├── composables/
│   │   ├── stores/
│   │   ├── types/
│   │   └── views/
│   │
│   ├── users/
│   │   ├── api/
│   │   ├── components/
│   │   ├── composables/
│   │   ├── stores/
│   │   ├── types/
│   │   └── views/
│   │
│   └── factory-vanguard/
│       ├── api/
│       ├── components/
│       ├── composables/
│       ├── stores/
│       ├── types/
│       └── views/
│
├── shared/
│   ├── components/
│   ├── composables/
│   ├── directives/
│   ├── types/
│   └── utils/
│
├── router/
├── plugins/
├── assets/
├── App.vue
└── main.ts
```

不要求每個 Feature 都建立全部資料夾。

例如某 Feature 沒有 Store：

> 就不要建立 stores/ 空資料夾。

---

# 二十三、React 專案推薦資料夾結構

適用：

- React
- TypeScript
- React Router
- 任一既有 State Library

```text
src/
├── api/
│   ├── http.ts
│   ├── userApi.ts
│   └── machineApi.ts
│
├── assets/
│   ├── images/
│   ├── icons/
│   └── styles/
│
├── components/
│   ├── common/
│   └── business/
│
├── hooks/
│   ├── usePermission.ts
│   ├── usePagination.ts
│   └── useListPageState.ts
│
├── router/
│   ├── routes.tsx
│   └── guards/
│
├── stores/
│   ├── useAuthStore.ts
│   └── usePermissionStore.ts
│
├── types/
│   └── global.types.ts
│
├── utils/
│   ├── formatDate.ts
│   └── formatNumber.ts
│
├── pages/
│   ├── LoginPage.tsx
│   ├── DashboardPage.tsx
│   └── users/
│       └── UserListPage.tsx
│
├── providers/
│   └── AppProviders.tsx
│
├── App.tsx
└── main.tsx
```

---

# 二十四、React 中大型專案推薦結構

```text
src/
├── features/
│   ├── auth/
│   │   ├── api/
│   │   ├── components/
│   │   ├── hooks/
│   │   ├── stores/
│   │   ├── types/
│   │   └── pages/
│   │
│   ├── users/
│   │   ├── api/
│   │   ├── components/
│   │   ├── hooks/
│   │   ├── stores/
│   │   ├── types/
│   │   └── pages/
│   │
│   └── factory-vanguard/
│       ├── api/
│       ├── components/
│       ├── hooks/
│       ├── stores/
│       ├── types/
│       └── pages/
│
├── shared/
│   ├── components/
│   ├── hooks/
│   ├── types/
│   └── utils/
│
├── router/
├── providers/
├── assets/
├── App.tsx
└── main.tsx
```

同樣不要求每一個 Feature 都建立所有資料夾。

---

# 二十五、Feature 與 Shared 的邊界

## Feature

只服務某個功能領域。

例如：

```text
features/users/
features/orders/
features/factory-vanguard/
```

其中可以包含該功能專用的：

- API
- Component
- Hook / Composable
- Store
- Type
- Page / View

只要目前只服務該 Feature：

> 優先留在 Feature 內。

---

## Shared

只有在內容真正跨多個 Feature 使用時才移入 Shared。

適合放 Shared：

- Button
- Modal
- Pagination
- DatePicker
- 通用 Hook / Composable
- 通用 Type
- Formatter
- HTTP Client

不適合：

```text
OrderStatusBadge
MachineMaintenanceForm
UserRoleEditor
```

這些具有明確功能語意，應優先放對應 Feature。

---

# 二十六、不要過早抽 Shared

不要因為一段程式碼：

> 「可能以後會共用」

就先放 Shared。

應至少確認：

- 已被多個 Feature 使用
- 抽象責任穩定
- 沒有大量 Feature-specific 條件

否則 Shared 很容易變成無邊界的雜物區。

---

# 二十七、命名規範

## 通用原則

### PascalCase

用於：

- Vue Component
- Vue View
- React Component
- React Page
- Type
- Interface
- Enum

例如：

```text
UserCard
MachineListView
MachineListPage
CreateUserRequest
```

---

### camelCase

用於：

- 變數
- 函式
- API object
- Hook
- Composable
- Store factory
- Utils

例如：

```text
machineApi
usePermission
useAuthStore
formatDate
loadUsers
```

---

### Boolean

建議使用：

```text
isLoading
isOpen
hasPermission
canEdit
shouldReload
```

---

### 常數

真正不應變動的設定可使用：

```text
API_BASE_URL
DEFAULT_PAGE_SIZE
MAX_RETRY_COUNT
```

---

# 二十八、Vue 命名規範

| 類型 | 規則 | 範例 |
|---|---|---|
| View | PascalCase + View | MachineListView.vue |
| Component | PascalCase | MaintenanceDialog.vue |
| Composable | use + PascalCase 語意，檔名 camelCase | usePermission.ts |
| Store | use + 名稱 + Store | useAuthStore.ts |
| API | camelCase + Api | machineApi.ts |
| Directive | camelCase | scanInput.ts |
| Type | PascalCase | MachineResponse |
| Utility | camelCase | formatDate |

---

# 二十九、React 命名規範

| 類型 | 規則 | 範例 |
|---|---|---|
| Page | PascalCase + Page | MachineListPage.tsx |
| Component | PascalCase | MaintenanceDialog.tsx |
| Hook | use + 名稱 | usePermission.ts |
| Store | use + 名稱 + Store | useAuthStore.ts |
| API | camelCase + Api | machineApi.ts |
| Provider | PascalCase + Provider | AuthProvider.tsx |
| Type | PascalCase | MachineResponse |
| Utility | camelCase | formatDate |

---

# 三十、API 命名規範

新增 API Module 使用：

```text
machineApi
workInProcessApi
userApi
permissionApi
```

不建議：

```text
machineAPI
userAPI
```

理由是：

> `Api` 視為一般角色名稱，統一遵守 camelCase。

若既有專案仍有：

```text
machineAPI
workInProcessAPI
```

可採漸進式收斂。

不要為了命名一致性一次大規模修改不相關程式碼。

---

# 三十一、前端不預設建立 Service 資料夾

本規範不將：

```text
services/
```

列為必要資料夾。

如果只是：

```text
Page
  ↓
Service
  ↓
API
```

而 Service 只是原封不動轉呼叫 API：

> 不需要建立 Service。

優先考慮：

- Page / View
- Component
- Composable / Hook
- Store
- API Module

只有當團隊未來真的定義出一種穩定且獨立的前端角色，才新增新的資料夾類型。

不要因為名稱聽起來像「架構層」就建立。

---

# 三十二、常見錯誤

## 1. Page / View 承擔全部內容

問題：

```text
Page
├── API
├── UI
├── State
├── Formatter
├── Permission
└── 所有子功能
```

結果會造成：

- 檔案過大
- 難以測試
- 邏輯難以重用
- 修改容易影響其他功能

---

## 2. Store 變成萬能模組

問題：

```text
Store
├── 所有 API
├── Navigation
├── UI State
├── Form
├── Validation
└── 所有流程
```

Store 的存在理由應是：

> 有共享狀態需要管理。

---

## 3. 所有 API 都抽 Composable / Hook

如果只是：

```text
呼叫一次 API
回傳資料
```

且沒有：

- reactive state
- reusable behavior
- loading
- error
- effect

不需要為了形式建立 Hook / Composable。

---

## 4. 所有 Component 都直接讀 Store

如果某個 Component 可以透過 props 接收資料，就不一定需要直接依賴 Store。

共用 Component 應優先維持低耦合。

---

## 5. Shared 成為雜物區

不要把：

> 不知道放哪裡的程式碼

全部放 Shared。

Shared 的前提是：

> 已經具有穩定的跨 Feature 共用責任。

---

## 6. Utils 具有副作用

如果 Utils 開始：

- 呼叫 API
- 修改 Store
- 導航
- 操作 UI

應重新判斷其真正責任。

---

# 三十三、新增程式碼判斷流程

新增程式碼時依序判斷。

## 1.

是否代表一個 Route 頁面？

Vue：

```text
View
```

React：

```text
Page
```

---

## 2.

是否代表一個 UI 區塊？

放：

```text
Component
```

---

## 3.

是否需要重複使用框架 reactive / lifecycle 邏輯？

Vue：

```text
Composable
```

React：

```text
Hook
```

---

## 4.

是否需要跨多個頁面或獨立 Component 共享狀態？

放：

```text
Store
```

---

## 5.

是否負責資料請求？

放：

```text
API Module
```

---

## 6.

是否只是通用且無狀態的轉換？

放：

```text
Utils
```

---

## 7.

是否只服務單一 Feature？

放：

```text
features/<feature-name>/
```

---

## 8.

是否已經被多個 Feature 穩定共用？

才移入：

```text
shared/
```

---

# 三十四、責任快速判斷表

| 問題 | Vue | React |
|---|---|---|
| 這是一個 Route 頁面嗎？ | View | Page |
| 這是 UI 區塊嗎？ | Component | Component |
| 這是可重用 reactive 邏輯嗎？ | Composable | Hook |
| 這是跨畫面共享狀態嗎？ | Pinia Store | Store |
| 這是資料請求定義嗎？ | API Module | API Module |
| 這是底層 HTTP 設定嗎？ | HTTP Client | HTTP Client |
| 這是無狀態通用函式嗎？ | Utils | Utils |
| 這是 DOM 行為嗎？ | Directive / Composable | Hook / Component |
| 這只屬於某一功能嗎？ | Feature | Feature |
| 這跨多個功能穩定共用嗎？ | Shared | Shared |

---

# 三十五、最終原則

整份前端架構規範可濃縮為：

> **View / Page 管頁面流程。**

> **Component 管 UI 與局部互動。**

> **Composable / Hook 管可重複的框架狀態與副作用邏輯。**

> **Store 管真正需要共享的狀態。**

> **API Module 管資料請求。**

> **HTTP Client 管共通 HTTP 行為。**

> **Utils 管無狀態通用函式。**

> **Feature 管功能內部程式碼。**

> **Shared 只放真正跨 Feature 的穩定共用內容。**

最重要的判斷不是：

> 「這個專案應該有幾層？」

而是：

> **「這段程式碼的前端責任是什麼？」**

每一個檔案、資料夾與模組，都應能清楚回答：

1. 它負責什麼？
2. 它不負責什麼？
3. 它服務哪個前端範圍？
4. 它是否真的需要共享？
5. 如果移除這層，責任是否仍然清楚？

只有真正具有獨立責任的抽象才應存在。
