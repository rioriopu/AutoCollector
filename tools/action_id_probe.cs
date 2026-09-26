using System;
using Lumina;
using Lumina.Excel.Sheets;

class P {
    static void Main() {
        var path = Environment.ExpandEnvironmentVariables(
            @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack");
        var gd = new GameData(path, new LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.Japanese });
        var sheet = gd.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        foreach (var id in new uint[]{5,6,7,8,9}) {
            var r = sheet.GetRowOrDefault(id);
            Console.WriteLine($"Action {id}: {(r.HasValue ? r.Value.Name.ExtractText() : "(なし)")}");
        }
        var ga = gd.GetExcelSheet<GeneralAction>();
        foreach (var id in new uint[]{1,2,6,9}) {
            var r = ga.GetRowOrDefault(id);
            Console.WriteLine($"GeneralAction {id}: {(r.HasValue ? r.Value.Name.ExtractText() : "(なし)")}");
        }
    }
}
