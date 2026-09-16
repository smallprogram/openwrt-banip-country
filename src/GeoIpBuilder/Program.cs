using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// 设定在仓库根目录生成数据
string rootDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..");
string countryDir = Path.Combine(rootDir, "country_data");
string notCountryDir = Path.Combine(rootDir, "not_country_data");

// 本地函数：确保目录存在并清空旧的 txt 文件
void EnsureAndClearDirectory(string path)
{
    if (Directory.Exists(path))
    {
        foreach (var file in Directory.GetFiles(path, "*.txt"))
        {
            File.Delete(file);
        }
    }
    else
    {
        Directory.CreateDirectory(path);
    }
}
Console.WriteLine("Cleaning up old data...");
EnsureAndClearDirectory(countryDir);
EnsureAndClearDirectory(notCountryDir);

using var client = new HttpClient();
client.DefaultRequestHeaders.Add("User-Agent", "OpenWrt-BanIP-Builder/1.0");

Console.WriteLine("Fetching file list from GitHub API...");
string apiUrl = "https://api.github.com/repos/Loyalsoldier/geoip/contents/text?ref=release";
string jsonString = await client.GetStringAsync(apiUrl);
using var document = JsonDocument.Parse(jsonString);

var validFiles = new Dictionary<string, string>();

// 1. 筛选文件
foreach (var element in document.RootElement.EnumerateArray())
{
    if (element.GetProperty("type").GetString() == "file")
    {
        string name = element.GetProperty("name").GetString() ?? string.Empty;
        if (name.EndsWith(".txt"))
        {
            string nameNoExt = name.Substring(0, name.Length - 4);
            if (nameNoExt.Length <= 3)
            {
                validFiles[nameNoExt] = element.GetProperty("download_url").GetString() ?? string.Empty;
            }
        }
    }
}

Console.WriteLine($"Found {validFiles.Count} valid country files. Downloading and parsing...");

var parsedCountries = new ConcurrentDictionary<string, (HashSet<string> v4, HashSet<string> v6)>();
var allV4 = new ConcurrentDictionary<string, byte>();
var allV6 = new ConcurrentDictionary<string, byte>();

var ipv4Regex = new Regex(@"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)(?:/\d{1,2})?$", RegexOptions.Compiled);

// 2. 并发下载并进行正则解析
await Parallel.ForEachAsync(validFiles, new ParallelOptions { MaxDegreeOfParallelism = 10 }, async (kvp, ct) =>
{
    string name = kvp.Key;
    string url = kvp.Value;

    try
    {
        string content = await client.GetStringAsync(url, ct);
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        var v4 = new HashSet<string>();
        var v6 = new HashSet<string>();

        foreach (var line in lines)
        {
            var trimLine = line.Trim();
            if (string.IsNullOrEmpty(trimLine) || trimLine.StartsWith("#")) continue;

            if (ipv4Regex.IsMatch(trimLine))
            {
                v4.Add(trimLine);
                allV4.TryAdd(trimLine, 0);
            }
            else
            {
                v6.Add(trimLine);
                allV6.TryAdd(trimLine, 0);
            }
        }
        parsedCountries[name] = (v4, v6);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error processing {name}: {ex.Message}");
    }
});

Console.WriteLine("Data parsing complete. Sorting global lists...");

//  1】：在循环外仅进行一次全局排序
var globalV4 = allV4.Keys.ToList();
globalV4.Sort();
var globalV6 = allV6.Keys.ToList();
globalV6.Sort();

Console.WriteLine("Sorting complete. Generating files concurrently...");

//  2】：使用 Parallel.ForEach 并发生成文件和计算差集
Parallel.ForEach(parsedCountries, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, kvp =>
{
    string name = kvp.Key;
    var (v4, v6) = kvp.Value;

    // 生成 country_data
    // 强制转换为 List 写入，v4 已经是 HashSet，为了保持跟原有行为一致可以转一下或者直接写
    File.WriteAllLines(Path.Combine(countryDir, $"{name}_v4.txt"), v4);
    File.WriteAllLines(Path.Combine(countryDir, $"{name}_v6.txt"), v6);

    // 生成 not_country_data (因为 globalV4 已排序，Where 筛选出来的数据自动保持有序，无需再 Order)
    var notV4 = globalV4.Where(ip => !v4.Contains(ip));
    var notV6 = globalV6.Where(ip => !v6.Contains(ip));

    File.WriteAllLines(Path.Combine(notCountryDir, $"not_{name}_v4.txt"), notV4);
    File.WriteAllLines(Path.Combine(notCountryDir, $"not_{name}_v6.txt"), notV6);
});

Console.WriteLine("All files generated successfully.");