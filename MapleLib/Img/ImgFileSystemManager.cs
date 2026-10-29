using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;
using MapleLib;
using MapleLib.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace MapleLib.Img
{
    /// <summary>
    /// Manages loading of WzImage files from a filesystem-based IMG structure.
    /// This replaces WzFileManager for the new IMG-based architecture.
    /// </summary>
    public class ImgFileSystemManager : IDisposable
    {
        #region Constants
        /// <summary>
        /// Standard categories that should exist in a valid extraction
        /// </summary>
        public static readonly string[] STANDARD_CATEGORIES = new[]
        {
            "Base", "String", "Map", "Mob", "Npc", "Reactor", "Sound", "Skill",
            "Character", "Item", "UI", "Effect", "Etc", "Quest", "Morph", "TamingMob", "List"
        };

        private const string MANIFEST_FILENAME = "manifest.json";
        private const string INDEX_FILENAME = "index.json";
        #endregion

        #region Fields
        private readonly string _versionPath;
        private readonly HaCreatorConfig _config;
        private readonly VersionInfo _versionInfo;
        private readonly WzMapleVersion _mapleVersion;
        private readonly byte[] _wzIv;

        private readonly ReaderWriterLockSlim _cacheLock = new();
        private readonly LRUCache<string, WzImage> _imageCache;
        private readonly object[] _loadGates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();   // striped per-key locks for LoadImage

        /// <summary>
        /// The full path each image was loaded from (or created at). save_image writes back to exactly this path: the
        /// cache keys are lower-cased and the caller's spelling of the name may differ (case, no ".img", "..").
        /// Weak, so it does not keep evicted images alive.
        /// </summary>
        private readonly ConditionalWeakTable<WzImage, string> _loadedPaths = new ConditionalWeakTable<WzImage, string>();
        private readonly ConcurrentDictionary<string, VirtualWzDirectory> _directoryCache = new();
        private readonly ConcurrentDictionary<string, List<string>> _categoryIndex = new();
        private readonly ConcurrentDictionary<string, byte> _indexedCategories = new();
        // Maps any-case category name to the real directory name on disk. Needed on case-sensitive
        // file systems (Linux), where the index keys are lower-cased but the directory is e.g. "String".
        private readonly ConcurrentDictionary<string, string> _categoryDirectoryNames = new(StringComparer.OrdinalIgnoreCase);

        // Default cache settings
        private const int DEFAULT_MAX_CACHE_ITEMS = 500;
        private const long DEFAULT_MAX_CACHE_BYTES = 512 * 1024 * 1024; // 512MB

        private bool _isInitialized;
        private bool _disposed;

        // Statistics
        private int _cacheHits;
        private int _cacheMisses;
        private int _diskReads;

        // Hot swap
        private FileSystemWatcherService _watcherService;
        private bool _hotSwapEnabled;
        #endregion

        #region Events
        /// <summary>
        /// Raised when the category index changes due to file additions, deletions, or modifications
        /// </summary>
        public event EventHandler<CategoryIndexChangedEventArgs> CategoryIndexChanged;
        #endregion

        #region Properties
        /// <summary>
        /// Gets the root directory path for this version
        /// </summary>
        public string VersionPath => _versionPath;

        /// <summary>
        /// Gets the version information
        /// </summary>
        public VersionInfo VersionInfo => _versionInfo;

        /// <summary>
        /// Gets whether the manager is initialized
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Gets the MapleStory version used for encryption
        /// </summary>
        public WzMapleVersion MapleVersion => _mapleVersion;

        /// <summary>
        /// Gets whether hot swap (file system watching) is enabled
        /// </summary>
        public bool HotSwapEnabled => _hotSwapEnabled;
        #endregion

        #region Constructor
        /// <summary>
        /// Creates a new ImgFileSystemManager for the specified version directory
        /// </summary>
        /// <param name="versionPath">Path to the version directory</param>
        /// <param name="config">Configuration options</param>
        public ImgFileSystemManager(
            string versionPath,
            HaCreatorConfig config = null,
            WzMapleVersion? explicitMapleVersion = null,
            byte[] explicitCustomIv = null)
        {
            if (!Directory.Exists(versionPath))
                throw new DirectoryNotFoundException($"Version directory not found: {versionPath}");

            _versionPath = Path.GetFullPath(versionPath);
            _config = config ?? new HaCreatorConfig();

            // Initialize LRU cache with size-based eviction
            // Use config values if available, otherwise defaults
            long maxCacheBytes = _config.Cache?.MaxMemoryCacheMB > 0
                ? _config.Cache.MaxMemoryCacheMB * 1024L * 1024L
                : DEFAULT_MAX_CACHE_BYTES;
            // Images with unsaved changes are pinned: evicting one would make the next access reload the file from disk and drop the edits.
            _imageCache = new LRUCache<string, WzImage>(maxCacheBytes, EstimateWzImageSize, IsPinnedImage);

            // Load version manifest
            string manifestPath = Path.Combine(_versionPath, MANIFEST_FILENAME);
            if (File.Exists(manifestPath))
            {
                try
                {
                    MemoryLimits.EnsureFileSize(manifestPath, MemoryLimits.MAX_METADATA_JSON_BYTES, "IMG version manifest");
                    string json = File.ReadAllText(manifestPath);
                    _versionInfo = JsonSerializer.Deserialize(json, MapleJsonContext.Default.VersionInfo)
                        ?? throw new InvalidDataException("Version manifest is null.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException($"Version manifest is malformed: {manifestPath}", ex);
                }
                _versionInfo.Categories ??= new Dictionary<string, CategoryInfo>(StringComparer.OrdinalIgnoreCase);
                _versionInfo.Features ??= new VersionFeatures();
                _versionInfo.DirectoryPath = _versionPath;
                VersionManager.InferAndPersistVUpdate(_versionInfo);
            }
            else
            {
                // Create default version info
                _versionInfo = new VersionInfo
                {
                    Version = Path.GetFileName(versionPath),
                    DisplayName = Path.GetFileName(versionPath),
                    DirectoryPath = _versionPath,
                    ExtractedDate = DateTime.Now
                };
            }

            // Default to BMS (IV {0,0,0,0}) so IMG filesystem data remains consistent across versions/localizations.
            // Respect explicit non-custom versions when provided, and only use custom IV for explicit CUSTOM.
            if (explicitMapleVersion.HasValue)
            {
                if (explicitMapleVersion.Value == WzMapleVersion.CUSTOM &&
                    explicitCustomIv != null &&
                    explicitCustomIv.Length == 4)
                {
                    _mapleVersion = WzMapleVersion.CUSTOM;
                    _wzIv = explicitCustomIv.ToArray();
                }
                else if (explicitMapleVersion.Value != WzMapleVersion.CUSTOM &&
                         explicitMapleVersion.Value != WzMapleVersion.GENERATE)
                {
                    _mapleVersion = explicitMapleVersion.Value;
                    _wzIv = WzTool.GetIvByMapleVersion(_mapleVersion);
                }
                else
                {
                    _mapleVersion = WzMapleVersion.BMS;
                    _wzIv = WzTool.GetIvByMapleVersion(_mapleVersion);
                }
            }
            else
            {
                _mapleVersion = WzMapleVersion.BMS;
                _wzIv = WzTool.GetIvByMapleVersion(_mapleVersion);

                if (!string.IsNullOrEmpty(_versionInfo.Encryption) &&
                    Enum.TryParse<WzMapleVersion>(_versionInfo.Encryption, true, out var manifestMapleVersion) &&
                    manifestMapleVersion != WzMapleVersion.GENERATE)
                {
                    _mapleVersion = manifestMapleVersion;
                    _wzIv = WzTool.GetIvByMapleVersion(_mapleVersion);
                }
            }
        }
        #endregion

        #region Initialization
        /// <summary>
        /// Initializes the manager by scanning the directory structure
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
                return;

            BuildCategoryIndex();
            _isInitialized = true;
        }

        /// <summary>
        /// Initializes the manager asynchronously
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            await Task.Run(() => BuildCategoryIndex());
            _isInitialized = true;
        }

        /// <summary>
        /// Discovers category directories. Individual category file indexes
        /// are built only when an operation needs a recursive category scan.
        /// </summary>
        private void BuildCategoryIndex()
        {
            // Look for category directories
            foreach (var dir in HaCreatorPaths.EnumerateDirectoriesExcludingBackups(_versionPath))
            {
                string categoryName = Path.GetFileName(dir);

                string categoryKey = categoryName.ToLowerInvariant();
                _categoryIndex.TryAdd(categoryKey, new List<string>());
                _categoryDirectoryNames.TryAdd(categoryName, categoryName);

                // Preserve manifest counts without touching every IMG file.
                if (!_versionInfo.Categories.ContainsKey(categoryName))
                {
                    _versionInfo.Categories[categoryName] = new CategoryInfo
                    {
                        FileCount = 0,
                        LastModified = Directory.GetLastWriteTime(dir)
                    };
                }
            }
        }

        private List<string> EnsureCategoryIndexed(string category)
        {
            EnsureInitialized();
            string categoryKey = category.ToLowerInvariant();
            if (!_categoryIndex.TryGetValue(categoryKey, out List<string> categoryPaths))
                return null;
            if (_indexedCategories.ContainsKey(categoryKey))
                return categoryPaths;

            _cacheLock.EnterUpgradeableReadLock();
            try
            {
                if (_indexedCategories.ContainsKey(categoryKey))
                    return categoryPaths;

                _cacheLock.EnterWriteLock();
                try
                {
                    string categoryPath = GetContainedCategoryPath(category);
                    List<string> paths = LoadCategoryIndex(category, categoryPath);
                    _categoryIndex[categoryKey] = paths;
                    _indexedCategories[categoryKey] = 0;
                    return paths;
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }
            }
            finally
            {
                _cacheLock.ExitUpgradeableReadLock();
            }
        }

        /// <summary>
        /// Recursively scans a directory for IMG files
        /// </summary>
        private void ScanDirectoryForImages(string baseDir, string currentDir, List<string> imageFiles)
        {
            // Add .img files in current directory
            foreach (var file in HaCreatorPaths.EnumerateFilesExcludingBackups(currentDir, "*.img"))
            {
                // Store relative path from category root
                string relativePath = file.Substring(baseDir.Length).TrimStart(Path.DirectorySeparatorChar);
                imageFiles.Add(relativePath);
            }

            // Recurse into subdirectories
            foreach (var subDir in HaCreatorPaths.EnumerateDirectoriesExcludingBackups(currentDir))
            {
                ScanDirectoryForImages(baseDir, subDir, imageFiles);
            }
        }
        #endregion

        #region Image Loading
        /// <summary>
        /// Loads a WzImage from the filesystem
        /// </summary>
        /// <param name="category">The category (e.g., "Map", "Mob")</param>
        /// <param name="relativePath">Relative path within the category (e.g., "Map/Map0/100000000.img")</param>
        /// <returns>The loaded WzImage or null if not found</returns>
        public WzImage LoadImage(string category, string relativePath)
        {
            // Build the full path first: the cache key comes from it, so every spelling of the same file
            // ("Map0/x.img", "map0/x.img", "Map0/x", "Map0/../Map0/x.img") shares one cached image.
            string fullPath = GetContainedImagePath(category, relativePath);
            string cacheKey = GetCacheKeyFromPath(fullPath) ?? CreateCacheKey(category, relativePath);

            // Check LRU cache first
            if (_imageCache.TryGet(cacheKey, out var cachedImage))
            {
                Interlocked.Increment(ref _cacheHits);
                return cachedImage;
            }

            // Miss, load and add happen under one lock per key (striped), and the cache is checked again once the lock
            // is held: two threads must not both load the file and let the second Add replace an image the first
            // caller may already have edited.
            lock (_loadGates[(uint)StringComparer.Ordinal.GetHashCode(cacheKey) % (uint)_loadGates.Length])
            {
                if (_imageCache.TryGet(cacheKey, out cachedImage))
                {
                    Interlocked.Increment(ref _cacheHits);
                    return cachedImage;
                }

                Interlocked.Increment(ref _cacheMisses);

                if (!File.Exists(fullPath))
                    return null;

                // Load the image
                WzImage image = LoadImageFromFile(fullPath, Path.GetFileName(relativePath));
                if (image != null)
                {
                    // Remember where it came from, then add to the LRU cache (will auto-evict if over capacity).
                    // If the cache kept a different, pinned image for this key, that one wins.
                    _loadedPaths.AddOrUpdate(image, fullPath);
                    var held = _imageCache.Add(cacheKey, image);
                    if (!ReferenceEquals(held, image))
                    {
                        image.Dispose();
                        image = held;
                    }
                }

                return image;
            }
        }

        /// <summary>
        /// Loads a WzImage directly from a file path
        /// </summary>
        private WzImage LoadImageFromFile(string filePath, string imageName)
        {
            Interlocked.Increment(ref _diskReads);

            foreach (var mapleVersion in GetReadMapleVersionCandidates())
            {
                WzImage image = null;
                try
                {
                    byte[] wzIv = WzTool.GetIvByMapleVersion(mapleVersion);

                    // Standalone IMG files keep a shareable reader so canvas
                    // and sound payloads remain on disk until the property is
                    // actually rendered or played. Checksums are recalculated
                    // by packing/saving paths and are not needed for reading.
                    var deserializer = new WzImgDeserializer(
                        freeResources: false,
                        calculateChecksum: false);
                    image = deserializer.WzImageFromIMGFile(
                        filePath,
                        wzIv,
                        imageName,
                        out bool success);
                    success = success && image != null && image.ParseImage();

                    if (success && image != null)
                    {
                        if (mapleVersion != _mapleVersion)
                        {
                            Debug.WriteLine($"[ImgFileSystemManager] Loaded {filePath} with fallback IMG encryption {mapleVersion}; primary was {_mapleVersion}.");
                        }

                        return image;
                    }

                    Debug.WriteLine($"[ImgFileSystemManager] Failed to parse image: {filePath} (success={success}, image={image != null}, encryption={mapleVersion})");
                    image?.Dispose();
                }
                catch (Exception ex)
                {
                    image?.Dispose();
                    Debug.WriteLine($"[ImgFileSystemManager] Error loading image {filePath} with {mapleVersion}: {ex.Message}");
                }
            }

            return null;
        }

        private IEnumerable<WzMapleVersion> GetReadMapleVersionCandidates()
        {
            yield return _mapleVersion;

            foreach (var fallback in new[] { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS })
            {
                if (fallback != _mapleVersion)
                {
                    yield return fallback;
                }
            }
        }

        /// <summary>
        /// Loads all images in a category
        /// </summary>
        public IEnumerable<WzImage> LoadImagesInCategory(string category)
        {
            List<string> imagePaths = EnsureCategoryIndexed(category);
            if (imagePaths == null)
            {
                yield break;
            }

            foreach (var relativePath in imagePaths)
            {
                var image = LoadImage(category, relativePath);
                if (image != null)
                {
                    yield return image;
                }
            }
        }

        /// <summary>
        /// Loads images from a specific subdirectory within a category
        /// </summary>
        public IEnumerable<WzImage> LoadImagesInDirectory(string category, string subDirectory)
        {
            string directoryPath = GetContainedDirectoryPath(category, subDirectory);
            if (!Directory.Exists(directoryPath))
            {
                yield break;
            }

            string categoryPath = GetContainedCategoryPath(category);
            foreach (string filePath in HaCreatorPaths.EnumerateFilesExcludingBackups(
                directoryPath, "*.img", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(categoryPath, filePath);
                WzImage image = LoadImage(category, relativePath);
                if (image != null)
                {
                    yield return image;
                }
            }
        }

        /// <summary>
        /// Gets image names in a directory WITHOUT loading them.
        /// This is used for lazy loading - get the list of available images without memory cost.
        /// </summary>
        public IEnumerable<string> GetImageNamesInDirectory(string category, string subDirectory)
        {
            string directoryPath = GetContainedDirectoryPath(category, subDirectory);
            if (!Directory.Exists(directoryPath))
            {
                yield break;
            }

            foreach (string filePath in HaCreatorPaths.EnumerateFilesExcludingBackups(directoryPath, "*.img"))
            {
                yield return Path.GetFileNameWithoutExtension(filePath);
            }
        }

        /// <summary>
        /// Gets the relative path (with ".img", using the OS directory separator) of every image in a category WITHOUT
        /// opening any of them: the list comes from the category index (index.json or a directory scan, kept in memory).
        /// Sorted by ordinal comparison, so the order is stable between calls. Empty for an unknown category.
        /// </summary>
        public IReadOnlyList<string> GetImagePathsInCategory(string category)
        {
            List<string> indexed = EnsureCategoryIndexed(category);
            if (indexed == null)
                return Array.Empty<string>();

            string[] paths = indexed.ToArray();
            Array.Sort(paths, StringComparer.Ordinal);
            return paths;
        }

        /// <summary>
        /// Gets the size in bytes of an image file without opening the image, or -1 when the file does not exist.
        /// </summary>
        public long GetImageFileSize(string category, string relativePath)
        {
            var file = new FileInfo(GetContainedImagePath(category, relativePath));
            return file.Exists ? file.Length : -1;
        }

        /// <summary>
        /// Opens an image for a one-off pass over many images (a scan) WITHOUT leaving it in the cache. An image that is
        /// already cached (this includes every image with unsaved edits) is returned as it is and
        /// <paramref name="fromCache"/> is true: the caller must not dispose it. Otherwise the file is read and parsed,
        /// the image is NOT cached and the caller owns it: dispose it when done. Returns null when the file does not exist
        /// or cannot be parsed. Hit and miss counters and the LRU order are not touched.
        /// </summary>
        public WzImage OpenImageUncached(string category, string relativePath, out bool fromCache)
        {
            string fullPath = GetContainedImagePath(category, relativePath);
            string cacheKey = GetCacheKeyFromPath(fullPath) ?? CreateCacheKey(category, relativePath);

            if (_imageCache.TryPeek(cacheKey, out var cached))
            {
                fromCache = true;
                return cached;
            }

            fromCache = false;
            if (!File.Exists(fullPath))
                return null;
            return LoadImageFromFile(fullPath, Path.GetFileName(relativePath));
        }

        /// <summary>
        /// Loads images in parallel for better performance
        /// </summary>
        public async Task<List<WzImage>> LoadImagesParallelAsync(string category, int maxParallelism = 4)
        {
            List<string> imagePaths = EnsureCategoryIndexed(category);
            if (imagePaths == null)
            {
                return new List<WzImage>();
            }

            var images = new ConcurrentBag<WzImage>();

            await Parallel.ForEachAsync(imagePaths,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelism },
                async (path, ct) =>
                {
                    var image = await Task.Run(() => LoadImage(category, path), ct);
                    if (image != null)
                    {
                        images.Add(image);
                    }
                });

            return images.ToList();
        }
        #endregion

        #region Directory Access
        /// <summary>
        /// Gets a virtual WzDirectory that provides WzDirectory-compatible access to a category
        /// </summary>
        public VirtualWzDirectory GetDirectory(string category)
        {
            if (HaCreatorPaths.IsBackupsDirectoryName(category))
                return null;

            string categoryPath = GetContainedCategoryPath(category);
            string categoryLower = category.ToLowerInvariant();

            if (_directoryCache.TryGetValue(categoryLower, out var cached))
            {
                return cached;
            }

            if (!Directory.Exists(categoryPath))
            {
                return null;
            }

            var virtualDir = new VirtualWzDirectory(this, category, categoryPath);
            _directoryCache[categoryLower] = virtualDir;
            return virtualDir;
        }

        /// <summary>
        /// Gets all available categories
        /// </summary>
        public IEnumerable<string> GetCategories()
        {
            EnsureInitialized();
            return _categoryIndex.Keys;
        }

        /// <summary>
        /// Checks if a category exists
        /// </summary>
        public bool CategoryExists(string category)
        {
            EnsureInitialized();
            return _categoryIndex.ContainsKey(category.ToLowerInvariant());
        }

        /// <summary>
        /// Checks if an image exists
        /// </summary>
        public bool ImageExists(string category, string relativePath)
        {
            return File.Exists(GetContainedImagePath(category, relativePath));
        }

        /// <summary>
        /// Gets diagnostic information about an image lookup for debugging purposes
        /// </summary>
        public string GetImageDiagnostics(string category, string relativePath)
        {
            string fullPath = GetContainedImagePath(category, relativePath);

            string categoryPath = Path.Combine(_versionPath, ResolveCategoryName(category));
            bool categoryDirExists = Directory.Exists(categoryPath);
            bool fileExists = File.Exists(fullPath);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Version path: {_versionPath}");
            sb.AppendLine($"Category: {category}");
            sb.AppendLine($"Relative path: {relativePath}");
            sb.AppendLine($"Full path: {fullPath}");
            sb.AppendLine($"Category directory exists: {categoryDirExists}");
            sb.AppendLine($"File exists: {fileExists}");

            if (categoryDirExists)
            {
                var imgFiles = HaCreatorPaths.EnumerateFilesExcludingBackups(categoryPath, "*.img")
                    .Select(Path.GetFileName)
                    .Take(10)
                    .ToList();
                sb.AppendLine($"First 10 .img files in category root: {string.Join(", ", imgFiles)}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Gets subdirectories within a category
        /// </summary>
        public IEnumerable<string> GetSubdirectories(string category)
        {
            string categoryPath = GetContainedCategoryPath(category);
            if (!Directory.Exists(categoryPath))
            {
                return Enumerable.Empty<string>();
            }

            return EnumerateSubdirectories(categoryPath);
        }

        private static IEnumerable<string> EnumerateSubdirectories(string categoryPath)
        {
            int categoryPathLength = categoryPath.Length;
            foreach (string directory in HaCreatorPaths.EnumerateDirectoriesExcludingBackups(categoryPath, SearchOption.AllDirectories))
            {
                ReadOnlySpan<char> relativePath = directory.AsSpan(categoryPathLength).TrimStart(Path.DirectorySeparatorChar);
                yield return relativePath.ToString();
            }
        }
        #endregion

        #region Image Saving
        /// <summary>
        /// Saves a WzImage to the filesystem
        /// </summary>
        /// <param name="image">The image to save</param>
        /// <param name="category">The category (e.g., "Map", "Mob")</param>
        /// <param name="relativePath">Relative path within the category</param>
        /// <returns>True if saved successfully</returns>
        public bool SaveImage(WzImage image, string category, string relativePath)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            string fullPath = GetContainedImagePath(category, relativePath);

            return SaveImageToFile(image, fullPath);
        }

        /// <summary>
        /// Returns the full path an image was loaded from (or created at), or null when this manager did not load it.
        /// </summary>
        public string GetLoadedImagePath(WzImage image)
        {
            if (image == null)
                return null;
            return _loadedPaths.TryGetValue(image, out string path) ? path : null;
        }

        /// <summary>
        /// Saves an image back to the file it was loaded from, whatever spelling the caller used for its name.
        /// Throws when the image was not loaded by this manager, or when that file or its folder no longer exists
        /// (nothing is recreated and no directory is created). Returns false when the write itself fails (the file on
        /// disk is then unchanged). After a successful save every cache entry of the image is removed, so the next access
        /// reads the file that was just written (the in-memory image still points at the old offsets).
        /// </summary>
        public bool SaveLoadedImage(WzImage image, bool evictFromCache = true)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            string path = GetLoadedImagePath(image)
                ?? throw new InvalidOperationException($"Image {image.Name} was not loaded from this data source; it cannot be saved back to its own file.");
            if (!File.Exists(path))
                throw new FileNotFoundException($"The file this image was loaded from no longer exists (removed or renamed): {path}. Nothing was written.");

            bool saved = SaveImageCore(image, path, allowCreateDirectory: false);
            if (saved && evictFromCache)
            {
                // An image edited again while it was being saved is still pinned (Changed) and stays cached, so the
                // newer edit is not orphaned; RemoveUnpinned leaves it alone.
                foreach (var item in _imageCache.GetAllItems())
                {
                    if (ReferenceEquals(item.Value, image))
                        _imageCache.RemoveUnpinned(item.Key);
                }
            }
            return saved;
        }

        /// <summary>
        /// Saves a WzImage directly to a file path
        /// </summary>
        /// <param name="image">The image to save</param>
        /// <param name="filePath">Full file path</param>
        /// <returns>True if saved successfully</returns>
        public bool SaveImageToFile(WzImage image, string filePath)
        {
            return SaveImageCore(image, filePath, allowCreateDirectory: true);
        }

        /// <summary>Test seam: runs after the image was serialized and before the file is replaced.</summary>
        internal Action<WzImage> AfterSerializeForTests;

        private bool SaveImageCore(WzImage image, string filePath, bool allowCreateDirectory)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            string tmpPath = null;
            try
            {
                filePath = Path.GetFullPath(filePath);

                // A symbolic link anywhere below the data source (the image file included) or below the backups
                // directory is refused: the write and the backup copy would otherwise follow it out of the tree.
                string backupPath = GetExternalBackupFilePath(filePath);
                NoFollowFile.RefuseSymbolicLinksBelow(_versionPath, filePath);
                NoFollowFile.RefuseSymbolicLinksBelow(HaCreatorPaths.GetBackupsPath(_versionPath), backupPath);

                // Ensure directory exists (never created when saving an image back to the file it was loaded from)
                string directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                {
                    if (!allowCreateDirectory)
                        throw new DirectoryNotFoundException($"The folder of {filePath} no longer exists; nothing was written.");
                    Directory.CreateDirectory(directory);
                }

                // Save to a unique temporary file first. The destination is not touched
                // until serialization has completed successfully.
                tmpPath = $"{filePath}.{Guid.NewGuid():N}.tmp";

                // Taken before serializing: an edit made while the file is written must keep the image marked changed.
                int changeVersion = image.ChangeVersion;

                using (FileStream fs = File.Open(tmpPath, FileMode.CreateNew))
                {
                    using (WzBinaryWriter writer = new WzBinaryWriter(fs, _wzIv))
                    {
                        image.SaveImage(writer, true);
                    }
                }

                AfterSerializeForTests?.Invoke(image);

                FileReplacement.ReplaceWithBackup(tmpPath, filePath, backupPath);
                tmpPath = null;

                // Update cache key
                string cacheKey = GetCacheKeyFromPath(filePath);
                if (cacheKey != null)
                {
                    _imageCache.Add(cacheKey, image);
                }

                image.TryClearChanged(changeVersion);
                return true;
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(tmpPath))
                {
                    try
                    {
                        if (File.Exists(tmpPath))
                            File.Delete(tmpPath);
                    }
                    catch
                    {
                        // Preserve the original save error.
                    }
                }

                Debug.WriteLine($"Error saving image {filePath}: {ex.Message}");
                return false;
            }
        }

        private string GetExternalBackupFilePath(string filePath)
        {
            string versionName = Path.GetFileName(Path.TrimEndingDirectorySeparator(_versionPath));
            if (string.IsNullOrWhiteSpace(versionName))
                versionName = "UnknownVersion";

            string fullFilePath = Path.GetFullPath(filePath);
            string versionRoot = Path.GetFullPath(_versionPath);
            string versionRootWithSeparator = Path.EndsInDirectorySeparator(versionRoot) ? versionRoot : versionRoot + Path.DirectorySeparatorChar;
            string relativePath = fullFilePath.StartsWith(versionRootWithSeparator, NoFollowFile.PathComparison)
                ? fullFilePath.Substring(versionRootWithSeparator.Length)
                : Path.GetFileName(fullFilePath);

            string backupFilePath = Path.Combine(
                HaCreatorPaths.GetBackupsPath(_versionPath),
                "IMG",
                versionName,
                relativePath);
            return FileReplacement.GetBackupFilePath(backupFilePath);
        }

        /// <summary>
        /// Returns the on-disk directory name for a category given in any case; unknown names are returned unchanged.
        /// </summary>
        private string ResolveCategoryName(string category)
        {
            if (!string.IsNullOrWhiteSpace(category) && _categoryDirectoryNames.TryGetValue(category, out string actual))
                return actual;
            return category;
        }

        private string GetContainedImagePath(string category, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Category is required.", nameof(category));
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Relative path is required.", nameof(relativePath));

            string fullPath = Path.Combine(_versionPath, ResolveCategoryName(category), relativePath);
            if (!fullPath.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                fullPath += ".img";

            fullPath = Path.GetFullPath(fullPath);
            string root = _versionPath;
            string rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(rootWithSeparator, NoFollowFile.PathComparison))
                throw new InvalidOperationException($"Image path escapes the version directory: {fullPath}");

            return fullPath;
        }

        private string GetContainedCategoryPath(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Category is required.", nameof(category));

            string root = _versionPath;
            string fullPath = Path.GetFullPath(Path.Combine(root, ResolveCategoryName(category)));
            string rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, NoFollowFile.PathComparison))
                throw new InvalidOperationException($"Category path escapes the version directory: {fullPath}");
            return fullPath;
        }

        private string GetContainedDirectoryPath(string category, string relativePath)
        {
            string categoryPath = GetContainedCategoryPath(category);
            string fullPath = string.IsNullOrWhiteSpace(relativePath)
                ? categoryPath
                : Path.GetFullPath(Path.Combine(categoryPath, relativePath));
            string categoryWithSeparator = Path.EndsInDirectorySeparator(categoryPath)
                ? categoryPath
                : categoryPath + Path.DirectorySeparatorChar;
            if (!string.Equals(fullPath, categoryPath, NoFollowFile.PathComparison) &&
                !fullPath.StartsWith(categoryWithSeparator, NoFollowFile.PathComparison))
                throw new InvalidOperationException($"Directory path escapes the category: {fullPath}");
            if (HaCreatorPaths.ContainsBackupsDirectory(fullPath))
                throw new InvalidOperationException($"Backup directories are not part of the IMG data source: {fullPath}");
            return fullPath;
        }

        private static bool IsSafeIndexedImagePath(string categoryPath, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                !relativePath.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                return false;

            string root = Path.GetFullPath(categoryPath);
            string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            string rootWithSeparator = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootWithSeparator, NoFollowFile.PathComparison) &&
                !HaCreatorPaths.ContainsBackupsDirectory(fullPath);
        }

        /// <summary>
        /// Gets the cache key from a full file path
        /// </summary>
        private string GetCacheKeyFromPath(string filePath)
        {
            // Extract category and relative path from full path
            string root = _versionPath;
            string rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!filePath.StartsWith(rootWithSeparator, NoFollowFile.PathComparison))
                return null;

            string relative = filePath.Substring(rootWithSeparator.Length);
            string[] parts = relative.Split(Path.DirectorySeparatorChar, 2);
            if (parts.Length < 2)
                return null;

            return CreateCacheKey(parts[0], parts[1]);
        }

        internal static string CreateCacheKey(string category, string relativePath)
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            return string.Create(
                category.Length + 1 + relativePath.Length,
                (Category: category, RelativePath: relativePath, Culture: culture),
                static (destination, state) =>
                {
                    int categoryLength = state.Category.AsSpan().ToLower(destination, state.Culture);
                    if (categoryLength < 0)
                        throw new InvalidOperationException("The normalized category exceeded the cache-key buffer.");

                    destination[categoryLength] = '/';
                    int relativeLength = state.RelativePath.AsSpan().ToLower(
                        destination[(categoryLength + 1)..],
                        state.Culture);
                    if (relativeLength < 0 || categoryLength + 1 + relativeLength != destination.Length)
                        throw new InvalidOperationException("The normalized path did not fit the cache-key buffer.");
                });
        }

        /// <summary>
        /// Creates a new empty image file
        /// </summary>
        /// <param name="category">The category</param>
        /// <param name="relativePath">Relative path within category</param>
        /// <returns>The created WzImage or null if failed</returns>
        public WzImage CreateImage(string category, string relativePath)
        {
            string fullPath = GetContainedImagePath(category, relativePath);

            // Check if file already exists
            if (File.Exists(fullPath))
                return null;

            // Ensure directory exists
            string directory = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Create empty WzImage
            string imageName = Path.GetFileName(fullPath);
            WzImage newImage = new WzImage(imageName);
            newImage.Changed = true;

            // Save it
            if (SaveImageToFile(newImage, fullPath))
            {
                _loadedPaths.AddOrUpdate(newImage, fullPath);
                // Update category index
                string categoryLower = category.ToLowerInvariant();
                if (!_categoryIndex.ContainsKey(categoryLower))
                {
                    _categoryIndex[categoryLower] = new List<string>();
                }
                _categoryIndex[categoryLower].Add(relativePath);

                return newImage;
            }

            return null;
        }

        /// <summary>
        /// Deletes an image file
        /// </summary>
        /// <param name="category">The category</param>
        /// <param name="relativePath">Relative path within category</param>
        /// <returns>True if deleted successfully</returns>
        public bool DeleteImage(string category, string relativePath)
        {
            string fullPath = GetContainedImagePath(category, relativePath);

            try
            {
                if (!File.Exists(fullPath))
                    return false;

                File.Delete(fullPath);

                // Remove from cache
                RemoveFromCache(category, relativePath);

                // Remove from category index
                string categoryLower = category.ToLowerInvariant();
                if (_categoryIndex.TryGetValue(categoryLower, out var paths))
                {
                    paths.Remove(relativePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error deleting image {fullPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the full filesystem path for an image
        /// </summary>
        public string GetImagePath(string category, string relativePath)
        {
            return GetContainedImagePath(category, relativePath);
        }
        #endregion

        #region Cache Management
        /// <summary>
        /// Preloads all images in a category for faster subsequent access
        /// </summary>
        public void PreloadCategory(string category)
        {
            foreach (var _ in LoadImagesInCategory(category))
            {
                // Loading populates the cache
            }
        }

        /// <summary>
        /// Preloads categories asynchronously
        /// </summary>
        public async Task PreloadCategoryAsync(string category)
        {
            await LoadImagesParallelAsync(category);
        }

        /// <summary>
        /// Clears the image cache to free memory
        /// </summary>
        public void ClearCache()
        {
            // Images with unsaved changes stay: dropping them would silently lose the edits.
            _imageCache.ClearUnpinned();
        }

        /// <summary>
        /// Drops every cached image that has no unsaved edits and returns how many were dropped. The dropped images are
        /// not disposed (a running tool call may still hold one); the garbage collector frees them.
        /// </summary>
        public int ReleaseUnpinnedImages() => _imageCache.ClearUnpinned();

        /// <summary>Number of images in the cache.</summary>
        public int CachedImageCount => _imageCache.Count;

        /// <summary>Number of cached images with unsaved edits (never evicted, trimmed or cleared).</summary>
        public int PinnedImageCount => _imageCache.CountPinned();

        /// <summary>The cache size limit in bytes (as measured by <see cref="EstimateWzImageSize"/>).</summary>
        public long CacheLimitBytes => _imageCache.MaxSizeBytes;

        /// <summary>
        /// A snapshot of the cached images with the category and relative path each was loaded from.
        /// No image is loaded or touched.
        /// </summary>
        public IReadOnlyList<CachedImageInfo> GetCachedImages()
        {
            string prefix = Path.EndsInDirectorySeparator(_versionPath) ? _versionPath : _versionPath + Path.DirectorySeparatorChar;
            var result = new List<CachedImageInfo>();
            foreach (var item in _imageCache.GetAllItems())
            {
                string relative;
                if (_loadedPaths.TryGetValue(item.Value, out string fullPath) && fullPath.StartsWith(prefix, NoFollowFile.PathComparison))
                    relative = fullPath.Substring(prefix.Length);
                else
                    relative = item.Key.Replace('/', Path.DirectorySeparatorChar);   // lower-cased cache key

                int separator = relative.IndexOf(Path.DirectorySeparatorChar);
                if (separator <= 0)
                    continue;
                result.Add(new CachedImageInfo(relative.Substring(0, separator), relative.Substring(separator + 1), item.Value));
            }
            return result;
        }

        /// <summary>
        /// Removes a specific image from the cache (any spelling of its name)
        /// </summary>
        public void RemoveFromCache(string category, string relativePath)
        {
            // LRU cache handles disposal of IDisposable items internally
            _imageCache.Remove(ResolveCacheKey(category, relativePath));
        }

        /// <summary>
        /// Removes every cache entry that holds this image object.
        /// </summary>
        public void RemoveFromCache(WzImage image)
        {
            foreach (var item in _imageCache.GetAllItems())
            {
                if (ReferenceEquals(item.Value, image))
                    _imageCache.Remove(item.Key);
            }
        }

        private static bool IsPinnedImage(WzImage image) => image != null && image.Changed;

        /// <summary>
        /// The cache key of an image given as category and relative path: built from the contained full path, like
        /// <see cref="LoadImage"/> does. Falls back to the plain lower-cased spelling when the path is not valid.
        /// </summary>
        private string ResolveCacheKey(string category, string relativePath)
        {
            try
            {
                string key = GetCacheKeyFromPath(GetContainedImagePath(category, relativePath));
                if (key != null)
                    return key;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                // fall through
            }
            return CreateCacheKey(category ?? string.Empty, relativePath ?? string.Empty);
        }

        /// <summary>
        /// Gets cache statistics
        /// </summary>
        public DataSourceStats GetStats()
        {
            var cacheStats = _imageCache.GetStatistics();
            return new DataSourceStats
            {
                CategoryCount = _categoryIndex.Count,
                ImageCount = _versionInfo.Categories?.Values.Sum(v => v.FileCount) ??
                    _categoryIndex.Values.Sum(v => v.Count),
                CachedImageCount = cacheStats.ItemCount,
                CacheHitCount = (int)cacheStats.HitCount,
                CacheMissCount = (int)cacheStats.MissCount,
                DiskReadCount = _diskReads,
                MemoryUsageBytes = cacheStats.SizeBytes,
                CacheLimitBytes = cacheStats.MaxSizeBytes,
                PinnedImageCount = _imageCache.CountPinned()
            };
        }

        /// <summary>
        /// Trims the cache to a specified number of items by removing least recently accessed.
        /// Note: With LRU cache, this is handled automatically. This method is kept for explicit trimming.
        /// </summary>
        /// <param name="maxItems">Maximum number of items to keep - not used with LRU cache, which uses size-based eviction</param>
        public void TrimCache(int maxItems)
        {
            // LRU cache automatically evicts based on size, no manual trimming needed
            // The cache will evict least recently used items when it exceeds maxSizeBytes
            Debug.WriteLine($"TrimCache called - LRU cache auto-manages at {_imageCache.CurrentSizeBytes / 1024 / 1024}MB");
        }

        /// <summary>
        /// Gets all cached WzImages that have been modified (Changed = true).
        /// Returns key-value pairs where key is the cache key (category/relativePath) and value is the WzImage.
        /// </summary>
        /// <returns>Collection of changed images with their cache keys</returns>
        public IEnumerable<KeyValuePair<string, WzImage>> GetChangedImages()
        {
            var allItems = _imageCache.GetAllItems();
            return EnumerateChangedImages(allItems);
        }

        private static IEnumerable<KeyValuePair<string, WzImage>> EnumerateChangedImages(
            IEnumerable<KeyValuePair<string, WzImage>> items)
        {
            foreach (var item in items)
            {
                if (item.Value != null && item.Value.Changed)
                    yield return item;
            }
        }

        /// <summary>
        /// Gets the count of cached WzImages that have been modified.
        /// </summary>
        /// <returns>Number of changed images in cache</returns>
        public int GetChangedImagesCount()
        {
            int count = 0;
            foreach (WzImage image in _imageCache.GetAllValues())
            {
                if (image != null && image.Changed)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Saves all changed images in the cache back to disk.
        /// </summary>
        /// <returns>Number of images saved</returns>
        public int SaveAllChangedImages()
        {
            int savedCount = 0;

            // Each changed image goes back to the path it was loaded from (the cache key is lower-cased and
            // may hold another spelling of the name). The same image can sit under several keys: save it once.
            var seen = new HashSet<WzImage>(ReferenceEqualityComparer.Instance);
            foreach (var kvp in GetChangedImages().ToList())
            {
                if (!seen.Add(kvp.Value))
                    continue;

                try
                {
                    if (SaveLoadedImage(kvp.Value, evictFromCache: false))
                        savedCount++;
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is IOException)
                {
                    Debug.WriteLine($"Not saving {kvp.Key}: {ex.Message}");
                }
            }

            return savedCount;
        }

        /// <summary>
        /// Gets information about changed images for display purposes.
        /// </summary>
        /// <returns>List of tuples containing (category, relativePath, imageName)</returns>
        public List<(string Category, string RelativePath, string ImageName)> GetChangedImagesInfo()
        {
            var result = new List<(string, string, string)>();
            var changedImages = GetChangedImages();

            foreach (var kvp in changedImages)
            {
                string cacheKey = kvp.Key;
                int separatorIndex = cacheKey.IndexOf('/');
                if (separatorIndex > 0)
                {
                    string category = cacheKey.Substring(0, separatorIndex);
                    string relativePath = cacheKey.Substring(separatorIndex + 1);
                    string imageName = kvp.Value?.Name ?? Path.GetFileName(relativePath);
                    result.Add((category, relativePath, imageName));
                }
            }

            return result;
        }

        /// <summary>
        /// Gets memory estimate for cached images
        /// </summary>
        public long EstimateCacheMemoryUsage()
        {
            return _imageCache.CurrentSizeBytes;
        }
        #endregion

        #region Category Index Files
        /// <summary>
        /// Generates index files for all categories for faster subsequent loading
        /// </summary>
        public void GenerateCategoryIndices()
        {
            EnsureInitialized();

            foreach (var categoryName in _categoryIndex.Keys)
            {
                GenerateCategoryIndex(categoryName);
            }
        }

        /// <summary>
        /// Generates an index file for a specific category
        /// </summary>
        public void GenerateCategoryIndex(string category)
        {
            string categoryPath = GetContainedCategoryPath(category);
            if (!Directory.Exists(categoryPath))
                return;

            var index = CategoryIndex.BuildFromDirectory(categoryPath, category);
            string indexPath = Path.Combine(categoryPath, INDEX_FILENAME);
            index.Save(indexPath);
        }

        /// <summary>
        /// Loads category index from file if available, otherwise scans directory
        /// </summary>
        private List<string> LoadCategoryIndex(string category, string categoryPath)
        {
            string indexPath = Path.Combine(categoryPath, INDEX_FILENAME);
            var index = CategoryIndex.Load(indexPath);

            // Use index if valid and not stale
            if (index != null && !index.IsStale(categoryPath))
            {
                var indexedPaths = index.AllImagePaths.ToList();
                if (indexedPaths.All(path => IsSafeIndexedImagePath(categoryPath, path)))
                    return indexedPaths;
            }

            // Fall back to directory scan
            var imageFiles = new List<string>();
            ScanDirectoryForImages(categoryPath, categoryPath, imageFiles);
            return imageFiles;
        }

        /// <summary>
        /// Checks if category indices exist
        /// </summary>
        public bool HasCategoryIndices()
        {
            foreach (var category in STANDARD_CATEGORIES)
            {
                string indexPath = Path.Combine(_versionPath, category, INDEX_FILENAME);
                if (File.Exists(indexPath))
                    return true;
            }
            return false;
        }
        #endregion

        #region Async Preloading
        /// <summary>
        /// Preloads multiple categories in parallel
        /// </summary>
        public async Task PreloadCategoriesAsync(IEnumerable<string> categories, int maxParallelism = 2)
        {
            await Parallel.ForEachAsync(categories,
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelism },
                async (category, ct) =>
                {
                    await LoadImagesParallelAsync(category);
                });
        }

        /// <summary>
        /// Preloads commonly used categories (String, Map helpers)
        /// </summary>
        public async Task PreloadCommonCategoriesAsync()
        {
            // Load String category first - most commonly accessed
            if (CategoryExists("String"))
            {
                await LoadImagesParallelAsync("String");
            }

            // Load Map helper images
            var mapHelperImage = LoadImage("Map", "MapHelper.img");

            // Load UI tooltips
            var uiTooltipImage = LoadImage("UI", "UIToolTip.img");
        }
        #endregion

        #region Hot Swap
        /// <summary>
        /// Enables or disables hot swap (file system watching)
        /// </summary>
        /// <param name="enable">True to enable, false to disable</param>
        /// <param name="debounceMs">Debounce delay in milliseconds (default 500)</param>
        public void EnableHotSwap(bool enable, int debounceMs = 500)
        {
            if (enable && !_hotSwapEnabled)
            {
                InitializeFileWatchers(debounceMs);
                _hotSwapEnabled = true;
            }
            else if (!enable && _hotSwapEnabled)
            {
                DisposeFileWatchers();
                _hotSwapEnabled = false;
            }
        }

        /// <summary>
        /// Initializes file system watchers for all category directories
        /// </summary>
        private void InitializeFileWatchers(int debounceMs)
        {
            _watcherService = new FileSystemWatcherService(debounceMs);
            _watcherService.ImgFileChanged += OnImgFileChanged;
            _watcherService.WatcherError += OnWatcherError;

            // Watch each category directory
            foreach (var category in _categoryIndex.Keys)
            {
                string categoryPath = Path.Combine(_versionPath, ResolveCategoryName(category));
                if (Directory.Exists(categoryPath))
                {
                    _watcherService.WatchPath(categoryPath, WatchType.Category, category);
                }
            }

            Debug.WriteLine($"Hot swap enabled for {_categoryIndex.Count} categories");
        }

        /// <summary>
        /// Disposes file system watchers
        /// </summary>
        private void DisposeFileWatchers()
        {
            if (_watcherService != null)
            {
                _watcherService.ImgFileChanged -= OnImgFileChanged;
                _watcherService.WatcherError -= OnWatcherError;
                _watcherService.Dispose();
                _watcherService = null;
            }

            Debug.WriteLine("Hot swap disabled");
        }

        /// <summary>
        /// Handles .img file change events from the file system watcher
        /// </summary>
        private void OnImgFileChanged(object sender, ImgFileChangedEventArgs e)
        {
            if (_disposed)
                return;

            try
            {
                switch (e.ChangeType)
                {
                    case WatcherChangeTypes.Created:
                        AddToCategoryIndex(e.Category, e.RelativePath);
                        OnCategoryIndexChanged(e.Category, CategoryChangeType.FileAdded, e.RelativePath);
                        break;

                    case WatcherChangeTypes.Deleted:
                        RemoveFromCategoryIndex(e.Category, e.RelativePath);
                        InvalidateCache(e.Category, e.RelativePath);
                        OnCategoryIndexChanged(e.Category, CategoryChangeType.FileRemoved, e.RelativePath);
                        break;

                    case WatcherChangeTypes.Changed:
                        // Windows sometimes reports new files as "Changed" instead of "Created"
                        // Check if file exists in index; if not, treat as Added
                        bool isNewFile = !ExistsInCategoryIndex(e.Category, e.RelativePath);
                        if (isNewFile)
                        {
                            AddToCategoryIndex(e.Category, e.RelativePath);
                            OnCategoryIndexChanged(e.Category, CategoryChangeType.FileAdded, e.RelativePath);
                        }
                        else
                        {
                            InvalidateCache(e.Category, e.RelativePath);
                            OnCategoryIndexChanged(e.Category, CategoryChangeType.FileModified, e.RelativePath);
                        }
                        break;

                    case WatcherChangeTypes.Renamed:
                        // Handle rename as delete old + add new
                        if (!string.IsNullOrEmpty(e.OldPath))
                        {
                            string oldRelativePath = Path.GetFileName(e.OldPath);
                            RemoveFromCategoryIndex(e.Category, oldRelativePath);
                            InvalidateCache(e.Category, oldRelativePath);
                        }
                        AddToCategoryIndex(e.Category, e.RelativePath);
                        OnCategoryIndexChanged(e.Category, CategoryChangeType.FileRenamed, e.RelativePath);
                        break;
                }

                Debug.WriteLine($"Hot swap: {e.ChangeType} - {e.Category}/{e.RelativePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling file change: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles watcher errors
        /// </summary>
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Debug.WriteLine($"File watcher error: {e.GetException()?.Message}");
        }

        /// <summary>
        /// Adds a file to the category index
        /// </summary>
        /// <param name="category">The category name</param>
        /// <param name="relativePath">The relative path within the category</param>
        public void AddToCategoryIndex(string category, string relativePath)
        {
            if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(relativePath))
                return;

            // Ensure it ends with .img
            if (!relativePath.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                return;

            string categoryLower = category.ToLowerInvariant();

            _cacheLock.EnterWriteLock();
            try
            {
                if (_categoryIndex.TryGetValue(categoryLower, out var files))
                {
                    if (!files.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        files.Add(relativePath);
                    }
                }
                else
                {
                    _categoryIndex[categoryLower] = new List<string> { relativePath };
                }
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Removes a file from the category index
        /// </summary>
        /// <param name="category">The category name</param>
        /// <param name="relativePath">The relative path within the category</param>
        public void RemoveFromCategoryIndex(string category, string relativePath)
        {
            if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(relativePath))
                return;

            string categoryLower = category.ToLowerInvariant();

            _cacheLock.EnterWriteLock();
            try
            {
                if (_categoryIndex.TryGetValue(categoryLower, out var files))
                {
                    // Remove case-insensitive
                    var toRemove = files.FirstOrDefault(f =>
                        f.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
                    if (toRemove != null)
                    {
                        files.Remove(toRemove);
                    }
                }
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Checks if a file exists in the category index
        /// </summary>
        /// <param name="category">The category name</param>
        /// <param name="relativePath">The relative path within the category</param>
        /// <returns>True if the file exists in the index</returns>
        public bool ExistsInCategoryIndex(string category, string relativePath)
        {
            if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(relativePath))
                return false;

            string categoryLower = category.ToLowerInvariant();

            _cacheLock.EnterReadLock();
            try
            {
                if (_categoryIndex.TryGetValue(categoryLower, out var files))
                {
                    return files.Any(f => f.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
                }
                return false;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Invalidates a cache entry for a specific file
        /// </summary>
        /// <param name="category">The category name</param>
        /// <param name="relativePath">The relative path within the category</param>
        public void InvalidateCache(string category, string relativePath)
        {
            _imageCache.Remove(ResolveCacheKey(category, relativePath));

            // Also invalidate the directory cache for this category
            string categoryLower = category.ToLowerInvariant();
            if (_directoryCache.TryGetValue(categoryLower, out var dir))
            {
                dir.Refresh();
            }
        }

        /// <summary>
        /// Raises the CategoryIndexChanged event
        /// </summary>
        protected void OnCategoryIndexChanged(string category, CategoryChangeType changeType, string relativePath)
        {
            CategoryIndexChanged?.Invoke(this, new CategoryIndexChangedEventArgs(category, changeType, relativePath));
        }

        /// <summary>
        /// Refreshes the category index by rescanning the directory
        /// </summary>
        /// <param name="category">The category to refresh, or null to refresh all</param>
        public void RefreshCategoryIndex(string category = null)
        {
            if (category != null)
            {
                string categoryPath = GetContainedCategoryPath(category);
                if (Directory.Exists(categoryPath))
                {
                    var imageFiles = new List<string>();
                    ScanDirectoryForImages(categoryPath, categoryPath, imageFiles);

                    _cacheLock.EnterWriteLock();
                    try
                    {
                        _categoryIndex[category.ToLowerInvariant()] = imageFiles;
                        _indexedCategories[category.ToLowerInvariant()] = 0;
                    }
                    finally
                    {
                        _cacheLock.ExitWriteLock();
                    }

                    OnCategoryIndexChanged(category, CategoryChangeType.IndexRefreshed, null);
                }
            }
            else
            {
                // Refresh all categories
                _categoryIndex.Clear();
                _indexedCategories.Clear();
                BuildCategoryIndex();
                OnCategoryIndexChanged(null, CategoryChangeType.IndexRefreshed, null);
            }
        }
        #endregion

        #region Helpers
        private void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                Initialize();
            }
        }

        // Bytes of managed heap per object, measured (not guessed): 2,255 real v83 images from 13 categories were loaded in
        // batches of 10 (or fewer), the heap was measured after each batch with a full compacting collection
        // (GC.GetTotalMemory(true)), and a least squares fit of the batches gave the cost of every kind of property. A
        // property is the object, its name string and its slot in the parent's collection (a List and a name Dictionary,
        // both with spare capacity); a container also owns that collection of its own. Name and string characters are counted
        // at 2 bytes each on top. The fit reproduces the measured heap within 1% over all batches (per category between 0.86
        // and 1.02 of the measured value, mean error per batch 2%); the figures below are the fit rounded up by about 3%, so
        // the estimate errs on the high side. The earlier flat 100 bytes per property was 2x to 5x too low.
        private const long ImageBaseBytes = 7 * 1024;       // WzImage, its FileStream (4 KB buffer) and WzBinaryReader
        private const long SubPropertyBytes = 432;          // Sub and Convex (the child collection is part of this)
        private const long CanvasPropertyBytes = 608;       // child collection and WzPngProperty included; payload bytes are added
        private const long VectorPropertyBytes = 352;
        private const long UolPropertyBytes = 208;
        private const long StringPropertyBytes = 184;
        private const long BinaryPropertyBytes = 392;       // payload bytes are added
        private const long LeafPropertyBytes = 176;         // Int, Short, Long, Float, Double, Null and the rest
        private const int MaxEstimateDepth = 50;

        /// <summary>
        /// Estimates the managed memory a parsed WzImage holds, in bytes. This is what the LRU cache counts against its
        /// limit, so the limit approximates real memory (see the constants above for how the figures were obtained).
        /// An unparsed image is only its reader.
        /// </summary>
        public static long EstimateWzImageSize(WzImage image)
        {
            if (image == null)
                return 0;

            long size = ImageBaseBytes;
            if (!image.Parsed)
                return size;

            try
            {
                if (image.WzProperties != null)
                {
                    foreach (var prop in image.WzProperties)
                    {
                        size += EstimatePropertySize(prop, 0);
                    }
                }
            }
            catch
            {
                // If estimation fails, use a conservative default
                size = 100 * 1024; // 100KB default
            }

            return size;
        }

        /// <summary>
        /// Recursively estimates the memory size of a WzImageProperty tree (a tree: a property has one parent, and links
        /// are not followed, so no property is counted twice).
        /// </summary>
        private static long EstimatePropertySize(WzImageProperty prop, int depth)
        {
            if (prop == null || depth > MaxEstimateDepth)
                return 0;

            long size = 2L * (prop.Name?.Length ?? 0);

            switch (prop)
            {
                case WzSubProperty:
                case WzConvexProperty:
                    size += SubPropertyBytes;
                    break;

                case WzCanvasProperty canvas:
                    size += CanvasPropertyBytes;
                    // Count only payloads actually materialized in memory.
                    var pngProp = canvas.PngProperty;
                    if (pngProp != null)
                    {
                        size += pngProp.compressedImageBytes?.LongLength ?? 0;
                        if (pngProp.png != null)
                            size += pngProp.Width * pngProp.Height * 4L;
                    }
                    break;

                case WzVectorProperty:
                    size += VectorPropertyBytes;
                    break;

                case WzStringProperty str:
                    size += StringPropertyBytes + 2L * (str.Value?.Length ?? 0); // UTF-16
                    break;

                case WzBinaryProperty binary:
                    size += BinaryPropertyBytes;
                    size += binary.fileBytes?.LongLength ?? 0;
                    size += binary.header?.LongLength ?? 0;
                    break;

                case WzUOLProperty uol:
                    // UOL.WzProperties resolves the target. Memory accounting must describe
                    // the link object itself, not traverse external, broken, or cyclic targets.
                    return size + UolPropertyBytes + 2L * (uol.Value?.Length ?? 0);

                default:
                    size += LeafPropertyBytes;
                    break;
            }

            // Add children
            if (prop.WzProperties != null)
            {
                foreach (var child in prop.WzProperties)
                {
                    size += EstimatePropertySize(child, depth + 1);
                }
            }

            return size;
        }
        #endregion

        #region IDisposable
        public void Dispose()
        {
            if (_disposed)
                return;

            // Disable hot swap and dispose watchers
            if (_hotSwapEnabled)
            {
                DisposeFileWatchers();
                _hotSwapEnabled = false;
            }

            _imageCache?.Dispose();  // LRU cache disposes all cached WzImage objects
            _directoryCache.Clear();
            _categoryIndex.Clear();
            _indexedCategories.Clear();
            _cacheLock?.Dispose();

            _disposed = true;
        }
        #endregion
    }

    /// <summary>An image held by the cache, with the category and relative path it was loaded from.</summary>
    public sealed class CachedImageInfo
    {
        public string Category { get; }
        public string RelativePath { get; }
        public WzImage Image { get; }

        public CachedImageInfo(string category, string relativePath, WzImage image)
        {
            Category = category;
            RelativePath = relativePath;
            Image = image;
        }
    }

    /// <summary>
    /// Specifies the type of change that occurred to the category index
    /// </summary>
    public enum CategoryChangeType
    {
        /// <summary>
        /// A new .img file was added
        /// </summary>
        FileAdded,

        /// <summary>
        /// An .img file was removed
        /// </summary>
        FileRemoved,

        /// <summary>
        /// An .img file was modified
        /// </summary>
        FileModified,

        /// <summary>
        /// An .img file was renamed
        /// </summary>
        FileRenamed,

        /// <summary>
        /// The entire index was refreshed
        /// </summary>
        IndexRefreshed
    }

    /// <summary>
    /// Event arguments for category index changes
    /// </summary>
    public class CategoryIndexChangedEventArgs : EventArgs
    {
        /// <summary>
        /// The category that changed, or null if all categories changed
        /// </summary>
        public string Category { get; }

        /// <summary>
        /// The type of change that occurred
        /// </summary>
        public CategoryChangeType ChangeType { get; }

        /// <summary>
        /// The relative path of the file that changed, or null for index refresh
        /// </summary>
        public string RelativePath { get; }

        public CategoryIndexChangedEventArgs(string category, CategoryChangeType changeType, string relativePath)
        {
            Category = category;
            ChangeType = changeType;
            RelativePath = relativePath;
        }
    }
}
