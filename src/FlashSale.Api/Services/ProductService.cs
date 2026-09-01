using AutoMapper;
using FlashSale.Api.Common.Constants;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Infrastructure.Cache;
using FlashSale.Api.Models.Dtos.Products;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Options;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace FlashSale.Api.Services;

public class ProductService : IProductService
{
    private readonly IProductRepository _productRepository;
    private readonly ICacheService _cache;
    private readonly IKeyedLock _keyedLock;
    private readonly CacheOptions _cacheOptions;
    private readonly IMapper _mapper;

    public ProductService(
        IProductRepository productRepository,
        ICacheService cache,
        IKeyedLock keyedLock,
        IOptions<CacheOptions> cacheOptions,
        IMapper mapper)
    {
        _productRepository = productRepository;
        _cache = cache;
        _keyedLock = keyedLock;
        _cacheOptions = cacheOptions.Value;
        _mapper = mapper;
    }

    public async Task<List<ProductDtoModel>> GetListAsync()
    {
        // 清單不快取：新增商品就得讓整份清單失效，
        // 而清單的讀取量遠低於單一商品，投資報酬率不成比例。
        var entities = await _productRepository.GetListAsync();

        return _mapper.Map<List<ProductDtoModel>>(entities);
    }

    /// <summary>
    /// Cache Aside：
    ///
    ///     讀快取
    ///       ├── Hit  → 直接回傳
    ///       └── Miss → 查資料庫 → 寫回快取 → 回傳
    /// </summary>
    public async Task<ProductDtoModel> GetByIdAsync(int id)
    {
        if (!_cacheOptions.Enabled)
        {
            return await LoadFromDatabaseAsync(id)
                ?? throw new NotFoundException("Product not found.");
        }

        var key = CacheKeys.Product(id);

        var cached = await _cache.GetAsync<ProductDtoModel>(key);

        if (cached.Found)
        {
            // 命中負向快取：先前已確認過查無此商品
            return cached.Value ?? throw new NotFoundException("Product not found.");
        }

        var dto = _cacheOptions.EnableSingleFlight
            ? await LoadWithSingleFlightAsync(id, key)
            : await LoadAndCacheAsync(id, key);

        return dto ?? throw new NotFoundException("Product not found.");
    }

    /// <summary>
    /// Single Flight：同一個 Key 同時 Miss 時只讓一個請求去查資料庫。
    ///
    /// 沒有這層保護，快取失效或冷啟動的瞬間，N 個併發請求會產生
    /// N 次資料庫查詢（Cache Stampede / Breakdown）。
    /// </summary>
    private async Task<ProductDtoModel?> LoadWithSingleFlightAsync(
        int id,
        string key)
    {
        using (await _keyedLock.AcquireAsync(key))
        {
            // 取得鎖之後要再讀一次快取。
            // 在排隊期間，先進去的那個請求很可能已經把值寫好了 ——
            // 少了這次 double-check，排隊的請求還是會一個個去查資料庫，
            // 只是從併發變成串行，查詢次數並沒有減少。
            var cached = await _cache.GetAsync<ProductDtoModel>(key);

            if (cached.Found)
            {
                return cached.Value;
            }

            return await LoadAndCacheAsync(id, key);
        }
    }

    private async Task<ProductDtoModel?> LoadAndCacheAsync(int id, string key)
    {
        var dto = await LoadFromDatabaseAsync(id);

        if (dto is not null)
        {
            await _cache.SetAsync(
                key,
                dto,
                TimeSpan.FromSeconds(_cacheOptions.TtlSeconds));

            return dto;
        }

        if (_cacheOptions.EnableNullCaching)
        {
            // Cache Penetration：查詢不存在的 Id 永遠不會命中快取，
            // 每一次都會穿透到資料庫。把「查無資料」本身也快取起來，
            // 但 TTL 要短，否則之後真的建立了這個商品會有一段時間查不到。
            await _cache.SetAsync<ProductDtoModel>(
                key,
                null,
                TimeSpan.FromSeconds(_cacheOptions.NullTtlSeconds));
        }

        return null;
    }

    private async Task<ProductDtoModel?> LoadFromDatabaseAsync(int id)
    {
        var entity = await _productRepository.GetByIdAsync(id);

        return entity is null
            ? null
            : _mapper.Map<ProductDtoModel>(entity);
    }

    public async Task<ProductDtoModel> CreateAsync(
        CreateProductDtoModel dto)
    {
        var exists = await _productRepository
            .ExistsByNameAsync(dto.Name);

        if (exists)
        {
            throw new BusinessException("Product name already exists.");
        }

        var entity = _mapper.Map<Product>(dto);

        entity.CreatedAt = DateTime.UtcNow;

        await _productRepository.CreateAsync(entity);

        // 新商品的 Id 可能剛好是先前被負向快取的那一個，
        // 不清掉的話新商品會在 NullTtl 期間查不到。
        await InvalidateAsync(entity.Id);

        return _mapper.Map<ProductDtoModel>(entity);
    }

    public async Task<ProductDtoModel> UpdateAsync(
        UpdateProductDtoModel dto)
    {
        var entity = await _productRepository.GetByIdAsync(dto.Id);

        if (entity is null)
        {
            throw new NotFoundException("Product not found.");
        }

        entity.Name = dto.Name;
        entity.Price = dto.Price;
        entity.Stock = dto.Stock;

        // 加入 rowversion 之後，這裡的更新也會帶版本檢查。
        // 若在讀取與寫入之間有人改過這筆商品（例如同時進行的搶購），
        // 這次更新會失敗 —— 這是正確行為，不該讓它變成 500。
        var updated = await _productRepository
            .TryUpdateWithVersionAsync(entity);

        if (!updated)
        {
            throw new BusinessException(
                "Product was modified by another request, please retry.");
        }

        // Cache Invalidation：先寫資料庫，再清快取。
        // 反過來（先清快取再寫資料庫）會有一個空窗：清完之後、寫入之前，
        // 其他請求會把「舊值」重新載入快取，於是舊值又活了一個 TTL。
        await InvalidateAsync(entity.Id);

        return _mapper.Map<ProductDtoModel>(entity);
    }

    private async Task InvalidateAsync(int productId)
    {
        if (!_cacheOptions.Enabled)
        {
            return;
        }

        await _cache.RemoveAsync(CacheKeys.Product(productId));
    }
}
