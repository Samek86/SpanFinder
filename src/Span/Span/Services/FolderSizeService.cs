using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Span.Services
{
    /// <summary>
    /// 폴더 크기를 백그라운드에서 계산하고 캐시하는 서비스.
    /// Details 뷰에서 폴더 크기 표시에 사용.
    /// </summary>
    public class FolderSizeService
    {
        private readonly ConcurrentDictionary<string, long> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);

        private const int MaxDepth = 8;

        /// <summary>
        /// 폴더 크기 계산 완료 시 발생. (folderPath, sizeInBytes)
        /// </summary>
        public event Action<string, long>? SizeCalculated;

        /// <summary>
        /// 캐시된 크기 반환. 없으면 null.
        /// </summary>
        public long? TryGetCachedSize(string folderPath)
        {
            return _cache.TryGetValue(folderPath, out var size) ? size : null;
        }

        /// <summary>
        /// 폴더 크기를 백그라운드에서 계산 요청. 이미 계산 중이면 무시.
        /// </summary>
        public void RequestCalculation(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            if (_cache.ContainsKey(folderPath)) return;
            if (_pending.ContainsKey(folderPath)) return;

            var cts = new CancellationTokenSource();
            if (!_pending.TryAdd(folderPath, cts))
            {
                cts.Dispose();
                return;
            }

            var token = cts.Token;
            _ = Task.Run(() =>
            {
                try
                {
                    long size = CalculateFolderSize(folderPath, 0, token);
                    if (!token.IsCancellationRequested)
                    {
                        _cache[folderPath] = size;
                        SizeCalculated?.Invoke(folderPath, size);
                    }
                }
                catch (OperationCanceledException) { /* 정상 취소 */ }
                catch
                {
                    if (!token.IsCancellationRequested)
                        _cache[folderPath] = -1; // 접근 불가 표시
                }
                finally
                {
                    _pending.TryRemove(folderPath, out var removed);
                    removed?.Dispose();
                }
            }, token);
        }

        /// <summary>
        /// 특정 폴더의 진행 중인 계산을 취소.
        /// </summary>
        public void CancelCalculation(string folderPath)
        {
            if (_pending.TryRemove(folderPath, out var cts))
            {
                try { cts.Cancel(); cts.Dispose(); }
                catch { }
            }
        }

        /// <summary>
        /// 진행 중인 모든 계산을 취소 (앱 종료/탭 닫기 시).
        /// </summary>
        public void CancelAll()
        {
            foreach (var kvp in _pending)
            {
                try { kvp.Value.Cancel(); kvp.Value.Dispose(); }
                catch { }
            }
            _pending.Clear();
        }

        /// <summary>
        /// 指定フォルダのキャッシュを無効化する。進行中の計算はキャンセルし、
        /// 古い結果が後からキャッシュへ書き戻されないようにする。
        /// CTS の破棄は計算タスクの finally に任せる。
        /// </summary>
        public void Invalidate(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            CancelPending(folderPath);
            _cache.TryRemove(folderPath, out _);
        }

        /// <summary>
        /// 指定フォルダ自身と、その配下にキャッシュされた子孫パスをまとめて無効化する。
        /// </summary>
        public void InvalidateUnder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            Invalidate(folderPath);

            var trimmed = folderPath.TrimEnd('\\', '/');
            var prefixBack = trimmed + "\\";
            var prefixFwd = trimmed + "/";

            // 列挙中に削除しない
            var cached = new List<string>();
            foreach (var key in _cache.Keys)
            {
                if (IsUnder(key, prefixBack, prefixFwd))
                    cached.Add(key);
            }
            foreach (var key in cached)
                Invalidate(key);

            var pending = new List<string>();
            foreach (var key in _pending.Keys)
            {
                if (IsUnder(key, prefixBack, prefixFwd))
                    pending.Add(key);
            }
            foreach (var key in pending)
                CancelPending(key);
        }

        private static bool IsUnder(string path, string prefixBack, string prefixFwd)
            => path.StartsWith(prefixBack, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(prefixFwd, StringComparison.OrdinalIgnoreCase);

        /// <summary>進行中の計算を止める。CTS はタスク側で破棄する。</summary>
        private void CancelPending(string folderPath)
        {
            if (_pending.TryGetValue(folderPath, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }

        private static long CalculateFolderSize(string path, int depth, CancellationToken token)
        {
            if (depth >= MaxDepth) return 0;
            token.ThrowIfCancellationRequested();

            long total = 0;
            try
            {
                var dirInfo = new DirectoryInfo(path);

                foreach (var file in dirInfo.EnumerateFiles())
                {
                    token.ThrowIfCancellationRequested();
                    try { total += file.Length; }
                    catch { /* 보호된 파일 무시 */ }
                }

                foreach (var dir in dirInfo.EnumerateDirectories())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue; // 심볼릭 링크 제외
                        total += CalculateFolderSize(dir.FullName, depth + 1, token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* 접근 불가 폴더 무시 */ }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 접근 불가 무시 */ }

            return total;
        }
    }
}
