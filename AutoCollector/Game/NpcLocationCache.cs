using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using ECommons.DalamudServices;

namespace AutoCollector.Game;

/// <summary>
/// NPC 配置の索引をファイルへ保存し、次回はそれを読む。
///
/// 索引は Level シートと配置ファイル（LGB）から作る。
/// 配置ファイルは数が多く 1 つ 1 つが大きいため、毎回作り直すとゲームが固まる。
/// 中身はゲームが更新されるまで変わらないので、1 度作れば使い回せる。
///
/// JSON ではなくバイナリにしてある。数万件を毎起動で読むため、
/// 解析にかかる時間をできるだけ削りたい。
/// </summary>
internal static class NpcLocationCache
{
    /// <summary>ファイルの識別子。別物を読み込まないための印。</summary>
    private const uint Magic = 0x434C4E41;

    /// <summary>形式の版。構造を変えたら上げる。古い版は捨てて作り直す。</summary>
    private const int FormatVersion = 1;

    /// <summary>1 体あたりの配置数の上限。これを超える分は捨てる。</summary>
    private const int MaxLocationsPerNpc = 1024;

    internal static string ResolvePath()
        => Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "npc-locations.bin");

    /// <summary>いま動いているゲームの版。これが変わったら索引を作り直す。</summary>
    internal static string ResolveGameVersion()
    {
        try
        {
            foreach (var repository in Svc.Data.GameData.Repositories.Values)
            {
                return repository.Version ?? string.Empty;
            }
        }
        catch
        {
            // 取れなければ空で扱う。空同士は一致しないものとして常に作り直す。
        }

        return string.Empty;
    }

    /// <summary>
    /// 保存済みの索引を読む。
    /// 読めなかった場合や、ゲームの版が変わっていた場合は false を返す。
    /// </summary>
    internal static bool TryLoad(
        string gameVersion,
        Dictionary<uint, List<NpcLocation>> into,
        out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrEmpty(gameVersion))
        {
            reason = "ゲームの版が分からないため保存済みの索引は使いません";
            return false;
        }

        var path = ResolvePath();

        if (!File.Exists(path))
        {
            reason = "保存済みの索引がありません";
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadUInt32() != Magic)
            {
                reason = "索引の形式が違います";
                return false;
            }

            if (reader.ReadInt32() != FormatVersion)
            {
                reason = "索引の版が古いため作り直します";
                return false;
            }

            var savedVersion = reader.ReadString();
            if (savedVersion != gameVersion)
            {
                reason = $"ゲームが更新されたため索引を作り直します（{savedVersion} → {gameVersion}）";
                return false;
            }

            var npcCount = reader.ReadInt32();
            if (npcCount < 0)
            {
                reason = "索引が壊れています";
                return false;
            }

            // 読めたぶんだけを入れる。途中で失敗したら呼び出し側が捨てる。
            for (var i = 0; i < npcCount; i++)
            {
                var npcId = reader.ReadUInt32();
                var locationCount = reader.ReadUInt16();

                var list = new List<NpcLocation>(locationCount);

                for (var j = 0; j < locationCount; j++)
                {
                    var territoryId = reader.ReadUInt32();
                    var x = reader.ReadSingle();
                    var y = reader.ReadSingle();
                    var z = reader.ReadSingle();
                    var source = (NpcLocationSource)reader.ReadByte();

                    list.Add(new NpcLocation(territoryId, new Vector3(x, y, z), source));
                }

                into[npcId] = list;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = $"索引を読めませんでした: {ex.Message}";
            return false;
        }
    }

    /// <summary>索引を保存する。失敗しても動作には影響しないため、握り潰して良い。</summary>
    internal static bool TrySave(
        string gameVersion,
        Dictionary<uint, List<NpcLocation>> index,
        out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrEmpty(gameVersion))
        {
            reason = "ゲームの版が分からないため保存しません";
            return false;
        }

        var path = ResolvePath();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // 書きかけのファイルを読ませないため、別名で書いてから差し替える。
            var temporary = path + ".tmp";

            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(gameVersion);
                writer.Write(index.Count);

                foreach (var (npcId, locations) in index)
                {
                    var count = Math.Min(locations.Count, MaxLocationsPerNpc);

                    writer.Write(npcId);
                    writer.Write((ushort)count);

                    for (var i = 0; i < count; i++)
                    {
                        var location = locations[i];
                        writer.Write(location.TerritoryId);
                        writer.Write(location.Position.X);
                        writer.Write(location.Position.Y);
                        writer.Write(location.Position.Z);
                        writer.Write((byte)location.Source);
                    }
                }
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"索引を保存できませんでした: {ex.Message}";
            return false;
        }
    }

    /// <summary>保存済みの索引を消す。作り直させたいときに使う。</summary>
    internal static bool TryDelete(out string reason)
    {
        reason = string.Empty;

        try
        {
            var path = ResolvePath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = $"索引を消せませんでした: {ex.Message}";
            return false;
        }
    }
}
