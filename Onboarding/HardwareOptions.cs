namespace MapleKiosk.Web.Onboarding;

/// <summary>A kind of device for the "existing hardware" task, with common
/// brands offered as suggestions (the customer can still type any brand).</summary>
public sealed record HardwareType(BriefOption Option, string[] Brands, string ModelExample);

/// <summary>
/// Device types and text for the "existing hardware" task: what the tenant
/// already owns and wants to keep, so the implementer can check compatibility
/// before install day.
/// </summary>
public static class HardwareOptions
{
    public static readonly HardwareType[] Types =
    [
        new(new("computer", "POS computer / all-in-one", "Ordinateur / tout-en-un de caisse", "Máy tính / máy POS"),
            ["Dell", "HP", "Lenovo", "Apple", "Elo", "Posiflex"], "OptiPlex 3080, Elo I-Series 4"),
        new(new("tablet", "Tablet / iPad", "Tablette / iPad", "Máy tính bảng / iPad"),
            ["Apple", "Samsung", "Lenovo", "Microsoft"], "iPad 10th gen, Galaxy Tab A9"),
        new(new("receipt", "Receipt printer", "Imprimante de reçus", "Máy in hóa đơn"),
            ["Epson", "Star Micronics", "Bixolon", "Citizen", "Xprinter"], "TM-T88VI, TSP143IIIU"),
        new(new("kitchen", "Kitchen / bar printer", "Imprimante cuisine / bar", "Máy in bếp / quầy bar", [OnboardingIndustries.Restaurant, OnboardingIndustries.Coffee]),
            ["Epson", "Star Micronics", "Bixolon"], "TM-U220, SP742"),
        new(new("label", "Label / sticker printer", "Imprimante d'étiquettes", "Máy in tem / nhãn"),
            ["Zebra", "Brother", "DYMO", "Rollo"], "ZD421, QL-820NWB"),
        new(new("drawer", "Cash drawer", "Tiroir-caisse", "Ngăn kéo đựng tiền"),
            ["APG", "Star Micronics", "MMF", "Epson"], "Vasario VB320"),
        new(new("scanner", "Barcode scanner", "Lecteur de codes-barres", "Máy quét mã vạch"),
            ["Zebra", "Honeywell", "Datalogic", "Socket Mobile"], "DS2208, Voyager 1450g"),
        new(new("card", "Card payment terminal", "Terminal de paiement", "Máy quẹt thẻ"),
            ["Moneris", "Clover", "Square", "Stripe", "Global Payments", "Ingenico", "Verifone", "PAX"], "Moneris Go, Clover Flex, PAX A920"),
        new(new("display", "Customer-facing display", "Afficheur client", "Màn hình phía khách"),
            ["Elo", "Posiflex", "Samsung", "Apple"], "Elo 1002L"),
        new(new("kiosk", "Self-service kiosk", "Borne libre-service", "Kiosk tự phục vụ"),
            ["Elo", "Samsung", "Partner Tech"], "Elo 22\" kiosk"),
        new(new("kds", "Kitchen display screen", "Écran de cuisine", "Màn hình bếp", [OnboardingIndustries.Restaurant, OnboardingIndustries.Coffee]),
            ["Samsung", "LG", "Elo", "Apple"], "Samsung 22\" + Fire TV"),
        new(new("network", "Router / Wi-Fi", "Routeur / Wi-Fi", "Router / Wi-Fi"),
            ["Bell", "Videotron", "Rogers", "TP-Link", "Netgear", "Ubiquiti", "Eero"], "Giga Hub, Archer AX55"),
        new(new("other", "Other", "Autre", "Khác"), [], ""),
    ];

    public static HardwareType? Find(string id) => Types.FirstOrDefault(t => t.Option.Id == id);

    public static string Label(string id, string culture) => Find(id)?.Option.Label(culture) ?? id;

    private static readonly Dictionary<string, (string En, string Fr, string Vi)> Text = new()
    {
        ["title"] = ("Existing hardware", "Matériel existant", "Thiết bị hiện có"),
        ["need"] = ("The devices you already own and want to keep using — so we can check they work with your new system before install day.",
                    "Les appareils que vous avez déjà et voulez garder — pour vérifier qu'ils fonctionnent avec votre nouveau système avant l'installation.",
                    "Các thiết bị bạn đang có và muốn tiếp tục dùng — để chúng tôi kiểm tra tương thích trước ngày lắp đặt."),
        ["tip"] = ("Tip: the brand and model are usually on a sticker underneath or on the back of the device. A photo of the sticker is perfect — upload it in “Services & staff”.",
                   "Astuce : la marque et le modèle sont souvent sur une étiquette sous l'appareil ou au dos. Une photo de l'étiquette est parfaite — téléversez-la dans « Services et personnel ».",
                   "Mẹo: hãng và model thường ghi trên nhãn dán ở mặt dưới hoặc mặt sau thiết bị. Chụp ảnh nhãn là đủ — tải lên ở mục “Dịch vụ & nhân viên”."),
        ["type"] = ("Device", "Appareil", "Thiết bị"),
        ["brand"] = ("Brand", "Marque", "Hãng"),
        ["model"] = ("Model", "Modèle", "Model"),
        ["qty"] = ("Qty", "Qté", "SL"),
        ["notes"] = ("Notes (age, condition, how it connects)", "Notes (âge, état, branchement)", "Ghi chú (đời máy, tình trạng, cách kết nối)"),
        ["pick"] = ("Choose…", "Choisir…", "Chọn…"),
        ["add"] = ("+ Add a device", "+ Ajouter un appareil", "+ Thêm thiết bị"),
        ["remove"] = ("Remove", "Retirer", "Xóa"),
        ["none"] = ("We don't have hardware to reuse", "Nous n'avons pas de matériel à réutiliser", "Chúng tôi không có thiết bị cần dùng lại"),
        ["eg"] = ("e.g. {0}", "ex. {0}", "vd: {0}"),
    };

    public static string T(string key, string culture) =>
        Text.TryGetValue(key, out var t) ? culture switch { "fr" => t.Fr, "vi" => t.Vi, _ => t.En } : key;
}
