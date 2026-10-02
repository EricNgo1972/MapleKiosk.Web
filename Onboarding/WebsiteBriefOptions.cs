namespace MapleKiosk.Web.Onboarding;

/// <summary>A choice in the website brief, labelled in EN / FR / VI. When
/// <see cref="Industries"/> is set the option only shows for those industries
/// (e.g. "Menu" for coffee and restaurants, "Request a quote" for garages).</summary>
public sealed record BriefOption(string Id, string En, string Fr, string Vi, string[]? Industries = null)
{
    public string Label(string culture) => culture switch { "fr" => Fr, "vi" => Vi, _ => En };
    public bool AppliesTo(string industry) => Industries is null || Industries.Contains(industry);
}

/// <summary>A visual style card: label, one-line description and the tokens the
/// mini preview is drawn with (colours + font), so customers pick by eye.</summary>
public sealed record BriefStyle(string Id, string En, string Fr, string Vi, string DescEn, string DescFr, string DescVi,
    string Bg, string Fg, string Accent, string Font, int Radius)
{
    public string Label(string c) => c switch { "fr" => Fr, "vi" => Vi, _ => En };
    public string Desc(string c) => c switch { "fr" => DescFr, "vi" => DescVi, _ => DescEn };
}

/// <summary>A preset colour swatch (background or accent).</summary>
public sealed record BriefColor(string Hex, string En, string Fr, string Vi)
{
    public string Label(string c) => c switch { "fr" => Fr, "vi" => Vi, _ => En };
}

public sealed record BriefPalette(string Id, string En, string Fr, string Vi, string[] Colors)
{
    public string Label(string c) => c switch { "fr" => Fr, "vi" => Vi, _ => En };
}

/// <summary>A 1–5 scale between two ends ("Classic … Modern").</summary>
public sealed record BriefSlider(string Id, BriefOption Left, BriefOption Right);

/// <summary>
/// Every option list and every prompt of the website brief, in one place so the
/// interview can be tuned (add a page type, reword a question) without touching
/// the page. Kept out of Translations.cs because the brief is self-contained and
/// most of its text is option data, not site chrome.
/// </summary>
public static class WebsiteBriefOptions
{
    /// <summary>The brief's sections, in order; the page shows one at a time
    /// (/onboarding/{token}/website/{n}, n = 1-based index).</summary>
    public static readonly string[] Sections = ["start", "goals", "pages", "style", "colours", "examples", "logo", "voice", "timeline"];

    public static string SectionTitle(int n, string culture) => T($"sec.{Sections[Math.Clamp(n, 1, Sections.Length) - 1]}", culture);

    private const string Nails = OnboardingIndustries.Nails, Coffee = OnboardingIndustries.Coffee,
        Resto = OnboardingIndustries.Restaurant, Garage = OnboardingIndustries.Garage;

    public static readonly BriefOption[] CurrentSite =
    [
        new("none", "We don't have a website", "Nous n'avons pas de site Web", "Chúng tôi chưa có trang web"),
        new("redesign", "We have one — replace or redesign it", "Nous en avons un — à remplacer ou refaire", "Đã có — muốn làm lại / thay mới"),
        new("keep", "Keep our site, just connect booking / ordering", "Garder notre site, ajouter seulement la réservation / commande", "Giữ trang hiện tại, chỉ gắn thêm đặt lịch / đặt món"),
    ];

    public static readonly BriefOption[] Domain =
    [
        new("have", "We own a domain", "Nous avons un nom de domaine", "Đã có tên miền"),
        new("need", "We need a new domain", "Il nous faut un nouveau domaine", "Cần mua tên miền mới"),
        new("unsure", "Not sure", "Je ne sais pas", "Không chắc"),
    ];

    public static readonly BriefOption[] Goals =
    [
        new("bookings", "Get more online bookings", "Plus de réservations en ligne", "Có thêm lịch hẹn online", [Nails, Garage]),
        new("orders", "Take online orders", "Prendre des commandes en ligne", "Nhận đặt món online", [Coffee, Resto]),
        new("reservations", "Take table reservations", "Prendre des réservations de table", "Nhận đặt bàn", [Resto]),
        new("quotes", "Get quote requests", "Recevoir des demandes de soumission", "Nhận yêu cầu báo giá", [Garage]),
        new("calls", "Get more phone calls", "Plus d'appels", "Có thêm cuộc gọi"),
        new("visits", "Bring people to the shop", "Attirer des clients sur place", "Thu hút khách đến tiệm"),
        new("prices", "Show services and prices clearly", "Montrer clairement services et prix", "Hiển thị rõ dịch vụ và giá", [Nails, Garage]),
        new("menu", "Show our menu", "Montrer notre menu", "Giới thiệu thực đơn", [Coffee, Resto]),
        new("showcase", "Show off our work", "Mettre en valeur notre travail", "Khoe sản phẩm / tác phẩm"),
        new("trust", "Build trust with reviews", "Bâtir la confiance avec des avis", "Tạo uy tín qua đánh giá"),
        new("giftcards", "Sell gift cards", "Vendre des cartes-cadeaux", "Bán thẻ quà tặng"),
        new("promos", "Promote offers and events", "Promouvoir offres et événements", "Quảng bá khuyến mãi, sự kiện"),
        new("hiring", "Hire staff", "Recruter du personnel", "Tuyển nhân viên"),
    ];

    public static readonly BriefOption[] Audience =
    [
        new("locals", "Neighbourhood locals", "Gens du quartier", "Người dân quanh khu vực"),
        new("professionals", "Busy professionals", "Professionnels pressés", "Người đi làm bận rộn"),
        new("families", "Families", "Familles", "Gia đình"),
        new("students", "Students / young adults", "Étudiants / jeunes adultes", "Sinh viên / người trẻ"),
        new("seniors", "Seniors", "Aînés", "Người lớn tuổi"),
        new("tourists", "Tourists and visitors", "Touristes et visiteurs", "Khách du lịch"),
        new("premium", "Clients who want a premium experience", "Clients en quête d'une expérience haut de gamme", "Khách thích dịch vụ cao cấp"),
        new("value", "Price-conscious clients", "Clients attentifs aux prix", "Khách quan tâm giá cả"),
        new("events", "Groups and events (weddings, parties)", "Groupes et événements (mariages, fêtes)", "Nhóm và sự kiện (cưới, tiệc)", [Nails, Resto]),
        new("fleet", "Business and fleet customers", "Entreprises et flottes", "Khách doanh nghiệp / đội xe", [Garage]),
    ];

    public static readonly BriefOption[] Pages =
    [
        new("home", "Home", "Accueil", "Trang chủ"),
        new("services", "Services & prices", "Services et prix", "Dịch vụ & bảng giá", [Nails, Garage]),
        new("menu", "Menu", "Menu", "Thực đơn", [Coffee, Resto]),
        new("booking", "Online booking", "Réservation en ligne", "Đặt lịch online", [Nails, Garage]),
        new("order", "Order online", "Commander en ligne", "Đặt món online", [Coffee, Resto]),
        new("about", "About us / our story", "À propos / notre histoire", "Giới thiệu / câu chuyện"),
        new("team", "Our team", "Notre équipe", "Đội ngũ"),
        new("gallery", "Gallery / portfolio", "Galerie / portfolio", "Thư viện ảnh"),
        new("reviews", "Reviews", "Avis", "Đánh giá"),
        new("giftcards", "Gift cards", "Cartes-cadeaux", "Thẻ quà tặng"),
        new("promotions", "Promotions / news", "Promotions / nouvelles", "Khuyến mãi / tin tức"),
        new("faq", "FAQ", "FAQ", "Câu hỏi thường gặp"),
        new("contact", "Contact & directions", "Contact et itinéraire", "Liên hệ & chỉ đường"),
        new("careers", "Careers", "Carrières", "Tuyển dụng"),
        new("blog", "Blog / tips", "Blogue / conseils", "Blog / mẹo hay"),
        new("policies", "Policies (cancellation, privacy)", "Politiques (annulation, confidentialité)", "Chính sách (hủy lịch, bảo mật)"),
    ];

    public static readonly BriefOption[] Features =
    [
        new("booking", "Book-now button everywhere", "Bouton « Réserver » partout", "Nút đặt lịch ở mọi trang", [Nails, Garage]),
        new("ordering", "Online ordering & pickup", "Commande en ligne et ramassage", "Đặt món & đến lấy", [Coffee, Resto]),
        new("reservations", "Table reservations", "Réservations de table", "Đặt bàn", [Resto]),
        new("quote", "Quote request form", "Formulaire de soumission", "Form yêu cầu báo giá", [Garage]),
        new("call", "Tap-to-call button", "Bouton d'appel", "Nút bấm gọi điện"),
        new("map", "Map & directions", "Carte et itinéraire", "Bản đồ & chỉ đường"),
        new("chat", "Chat (Messenger / WhatsApp)", "Clavardage (Messenger / WhatsApp)", "Chat (Messenger / WhatsApp)"),
        new("instagram", "Instagram feed", "Fil Instagram", "Hiển thị Instagram"),
        new("reviews", "Google reviews", "Avis Google", "Đánh giá Google"),
        new("giftshop", "Buy gift cards online", "Achat de cartes-cadeaux en ligne", "Mua thẻ quà tặng online"),
        new("newsletter", "Email / SMS sign-up", "Inscription courriel / SMS", "Đăng ký nhận email / SMS"),
        new("loyalty", "Loyalty / rewards info", "Programme de fidélité", "Chương trình khách hàng thân thiết"),
        new("popup", "Promo pop-up / banner", "Bannière promo", "Banner khuyến mãi"),
        new("accessibility", "Extra accessibility (large text, high contrast)", "Accessibilité renforcée", "Hỗ trợ tiếp cận (chữ lớn, tương phản cao)"),
    ];

    public static readonly BriefOption[] Languages =
    [
        new("en", "English", "Anglais", "Tiếng Anh"),
        new("fr", "French", "Français", "Tiếng Pháp"),
        new("vi", "Vietnamese", "Vietnamien", "Tiếng Việt"),
        new("es", "Spanish", "Espagnol", "Tiếng Tây Ban Nha"),
        new("zh", "Chinese", "Chinois", "Tiếng Trung"),
        new("other", "Other (say which in notes)", "Autre (précisez)", "Khác (ghi chú thêm)"),
    ];

    public static readonly BriefStyle[] Styles =
    [
        new("minimal", "Clean & minimal", "Épuré et minimal", "Tối giản, sạch sẽ",
            "Lots of white space, simple lines, calm.", "Beaucoup d'espace blanc, lignes simples, calme.", "Nhiều khoảng trắng, đường nét đơn giản, nhẹ nhàng.",
            "#ffffff", "#111111", "#111111", "'DM Sans', sans-serif", 2),
        new("luxury", "Luxury & elegant", "Luxe et élégant", "Sang trọng, thanh lịch",
            "Deep tones, gold accents, refined serif type.", "Tons profonds, touches dorées, typo raffinée.", "Tông màu sâu, điểm nhấn vàng, chữ có chân tinh tế.",
            "#16130f", "#f3e9d8", "#c9a45c", "'Cormorant Garamond', serif", 0),
        new("warm", "Warm & cozy", "Chaleureux et douillet", "Ấm áp, gần gũi",
            "Soft earthy colours, rounded shapes, inviting.", "Couleurs terreuses douces, formes arrondies.", "Màu đất dịu, bo tròn, thân thiện.",
            "#f6ede3", "#3b2a20", "#c8693f", "'Fraunces', serif", 14),
        new("bold", "Bold & vibrant", "Audacieux et vibrant", "Nổi bật, rực rỡ",
            "Strong colours, big type, full of energy.", "Couleurs fortes, gros titres, énergique.", "Màu mạnh, chữ lớn, tràn năng lượng.",
            "#ffde3b", "#111111", "#e5283a", "'Archivo Black', sans-serif", 6),
        new("natural", "Natural & organic", "Naturel et organique", "Tự nhiên, thuần khiết",
            "Greens and sand, textures, calm and healthy.", "Verts et sable, textures, sain et apaisant.", "Xanh lá, màu cát, cảm giác lành mạnh.",
            "#eef0e6", "#2f3a2c", "#6f8a5b", "'Lora', serif", 10),
        new("playful", "Fun & playful", "Ludique et joyeux", "Vui tươi, dễ thương",
            "Pastels, rounded type, a little quirky.", "Pastels, typo arrondie, un brin décalé.", "Màu pastel, chữ tròn, hơi tinh nghịch.",
            "#fde6f0", "#4a2b4f", "#ff7eb6", "'Quicksand', sans-serif", 22),
        new("classic", "Classic & timeless", "Classique et intemporel", "Cổ điển, bền vững",
            "Navy and cream, traditional, trustworthy.", "Marine et crème, traditionnel, rassurant.", "Xanh navy và kem, truyền thống, đáng tin.",
            "#f7f3ea", "#1d2a44", "#1d2a44", "'Libre Baskerville', serif", 2),
        new("modern", "Modern & techy", "Moderne et techno", "Hiện đại, công nghệ",
            "Dark mode, sharp edges, bright accent.", "Mode sombre, angles nets, accent vif.", "Nền tối, góc cạnh, điểm nhấn sáng.",
            "#0f1218", "#e8ecf3", "#3df2c2", "'Space Grotesk', sans-serif", 4),
    ];

    public static readonly BriefSlider[] Sliders =
    [
        new("classic_modern", new("", "Classic", "Classique", "Cổ điển"), new("", "Modern", "Moderne", "Hiện đại")),
        new("minimal_rich", new("", "Minimal", "Minimal", "Tối giản"), new("", "Rich in detail", "Riche en détails", "Nhiều chi tiết")),
        new("calm_energetic", new("", "Calm", "Calme", "Nhẹ nhàng"), new("", "Energetic", "Énergique", "Sôi động")),
        new("serious_playful", new("", "Serious", "Sérieux", "Nghiêm túc"), new("", "Playful", "Ludique", "Vui nhộn")),
        new("everyday_premium", new("", "Everyday, affordable", "Accessible", "Bình dân"), new("", "Premium, exclusive", "Haut de gamme", "Cao cấp")),
    ];

    public static readonly BriefOption[] Modes =
    [
        new("light", "Light", "Clair", "Sáng"),
        new("dark", "Dark", "Sombre", "Tối"),
        new("both", "Both — visitors can switch", "Les deux — au choix du visiteur", "Cả hai — khách tự chọn"),
        new("any", "Designer's choice", "Au choix du designer", "Để designer chọn"),
    ];

    public static readonly BriefColor[] Backgrounds =
    [
        new("#ffffff", "White", "Blanc", "Trắng"),
        new("#faf7f2", "Cream", "Crème", "Kem"),
        new("#f3ece3", "Warm beige", "Beige chaud", "Be ấm"),
        new("#fbefec", "Soft blush", "Rose poudré", "Hồng phấn nhạt"),
        new("#eef0e6", "Sage tint", "Sauge pâle", "Xanh xô thơm nhạt"),
        new("#eef3f7", "Cool mist", "Brume froide", "Xám xanh nhạt"),
        new("#f0f0f0", "Light grey", "Gris clair", "Xám nhạt"),
        new("#2a2522", "Charcoal", "Charbon", "Xám than"),
        new("#0f1a2e", "Midnight navy", "Marine nuit", "Xanh đêm"),
        new("#0d0d0d", "Black", "Noir", "Đen"),
    ];

    public static readonly BriefColor[] Accents =
    [
        new("#c0392b", "Maple red", "Rouge érable", "Đỏ phong"),
        new("#e07b4a", "Terracotta", "Terracotta", "Cam đất"),
        new("#c9a45c", "Gold", "Or", "Vàng kim"),
        new("#d98c9a", "Rose", "Rose", "Hồng đào"),
        new("#ff7eb6", "Pink", "Rose vif", "Hồng"),
        new("#8e6bbf", "Lavender", "Lavande", "Tím oải hương"),
        new("#6f8a5b", "Sage", "Sauge", "Xanh xô thơm"),
        new("#1f8a5b", "Emerald", "Émeraude", "Xanh ngọc lục bảo"),
        new("#1f6f8b", "Teal", "Sarcelle", "Xanh mòng két"),
        new("#2f5fd0", "Blue", "Bleu", "Xanh dương"),
        new("#1d2a44", "Navy", "Marine", "Xanh navy"),
        new("#111111", "Black", "Noir", "Đen"),
    ];

    public static string ColorLabel(string hex, string culture) =>
        Backgrounds.Concat(Accents).FirstOrDefault(c => c.Hex.Equals(hex, StringComparison.OrdinalIgnoreCase))?.Label(culture) ?? hex;

    public static readonly BriefOption[] CopyAspects =
    [
        new("layout", "Layout & structure", "Mise en page", "Bố cục"),
        new("colours", "Colours", "Couleurs", "Màu sắc"),
        new("fonts", "Fonts", "Typographie", "Kiểu chữ"),
        new("photos", "Photo style", "Style photo", "Phong cách ảnh"),
        new("feel", "Overall feel", "Ambiance générale", "Cảm giác tổng thể"),
        new("booking", "Booking / ordering flow", "Parcours de réservation / commande", "Cách đặt lịch / đặt món"),
    ];

    /// <summary>Legacy: the palette picker was replaced by background + accent.</summary>
    public static readonly BriefPalette[] Palettes =
    [
        new("neutral", "Soft neutrals", "Neutres doux", "Trung tính nhẹ", ["#faf8f5", "#e9e4dc", "#b9afa3", "#6d655c", "#2b2723"]),
        new("blush", "Blush & nude", "Rose poudré et nude", "Hồng phấn & nude", ["#fbefec", "#f2c9c0", "#d9a19a", "#a8676a", "#4a2c2f"]),
        new("earthy", "Earthy & warm", "Terre et chaleur", "Tông đất ấm", ["#f4ebe1", "#e0b48c", "#c8693f", "#7a4a2e", "#33231a"]),
        new("sage", "Sage & sand", "Sauge et sable", "Xanh xô thơm & cát", ["#f2f1e8", "#d6d8c4", "#a4b494", "#6f8a5b", "#2f3a2c"]),
        new("ocean", "Ocean blues", "Bleus océan", "Xanh biển", ["#eef5f8", "#bcdbe6", "#5fa8c4", "#1f6f8b", "#0f2f3d"]),
        new("sunset", "Sunset brights", "Couleurs coucher de soleil", "Hoàng hôn rực rỡ", ["#fff3df", "#ffc15e", "#ff8a3d", "#e5283a", "#5a1630"]),
        new("noir", "Black & gold", "Noir et or", "Đen & vàng", ["#0d0b09", "#2a241d", "#8a6d3b", "#c9a45c", "#f3e9d8"]),
        new("pastel", "Pastel mix", "Mélange pastel", "Pastel", ["#fde6f0", "#e3defc", "#d4f1ea", "#fff1c9", "#ff7eb6"]),
        new("mono", "Black & white", "Noir et blanc", "Đen trắng", ["#ffffff", "#e6e6e6", "#9a9a9a", "#444444", "#111111"]),
        new("brand", "Use our brand colours", "Nos couleurs de marque", "Dùng màu thương hiệu của chúng tôi", []),
    ];

    public static readonly BriefOption[] Fonts =
    [
        new("serif", "Elegant serif", "Empattement élégant", "Chữ có chân thanh lịch"),
        new("sans", "Clean sans-serif", "Sans empattement épuré", "Chữ không chân hiện đại"),
        new("script", "Handwritten / script accents", "Touches manuscrites", "Điểm nhấn chữ viết tay"),
        new("display", "Big, bold headlines", "Gros titres audacieux", "Tiêu đề to, đậm"),
        new("any", "No preference — designer's choice", "Aucune préférence", "Không yêu cầu — để designer chọn"),
    ];

    public static readonly BriefOption[] Logo =
    [
        new("have", "We have a logo we like (upload it below)", "Nous avons un logo que nous aimons (téléversez-le)", "Đã có logo ưng ý (tải lên bên dưới)"),
        new("refresh", "We have one, but it could be refreshed", "Nous en avons un, à rafraîchir", "Đã có nhưng muốn làm mới"),
        new("need", "We need a new logo", "Il nous faut un nouveau logo", "Cần thiết kế logo mới"),
    ];

    public static readonly BriefOption[] Photos =
    [
        new("have", "We have good photos (upload below)", "Nous avons de bonnes photos (téléversez-les)", "Đã có ảnh đẹp (tải lên bên dưới)"),
        new("shoot", "We'd like a photoshoot", "Nous aimerions une séance photo", "Muốn chụp ảnh chuyên nghiệp"),
        new("stock", "Stock photos are fine", "Des photos libres de droits suffisent", "Dùng ảnh mẫu cũng được"),
        new("mix", "A mix of ours and stock", "Un mélange des deux", "Kết hợp ảnh của tiệm và ảnh mẫu"),
    ];

    public static readonly BriefOption[] ImageryMood =
    [
        new("bright", "Bright & airy", "Lumineux et aéré", "Sáng sủa, thoáng"),
        new("moody", "Dark & moody", "Sombre et intimiste", "Tối, có chiều sâu"),
        new("details", "Close-up details of our work", "Gros plans sur notre travail", "Cận cảnh sản phẩm / tác phẩm"),
        new("people", "People & smiles", "Des gens et des sourires", "Con người & nụ cười"),
        new("space", "Our space / interior", "Notre espace / intérieur", "Không gian tiệm"),
        new("products", "Products", "Produits", "Sản phẩm"),
    ];

    public static readonly BriefOption[] AssetTypes =
    [
        new("logofiles", "Logo files (SVG, PNG, AI)", "Fichiers du logo (SVG, PNG, AI)", "File logo (SVG, PNG, AI)"),
        new("guide", "Brand guide", "Guide de marque", "Bộ nhận diện thương hiệu"),
        new("shop", "Photos of our shop", "Photos de notre commerce", "Ảnh cửa tiệm"),
        new("work", "Photos of our work / products", "Photos de nos réalisations / produits", "Ảnh sản phẩm / tác phẩm"),
        new("team", "Team photos", "Photos de l'équipe", "Ảnh đội ngũ"),
        new("fonts", "Our fonts", "Nos polices", "Font chữ riêng"),
        new("video", "Videos", "Vidéos", "Video"),
        new("print", "Menus, flyers, price lists we designed", "Menus, dépliants, listes de prix déjà conçus", "Menu, tờ rơi, bảng giá đã thiết kế"),
        new("none", "Nothing yet — please create or source them", "Rien encore — à créer ou trouver", "Chưa có — nhờ MapleKiosk chuẩn bị"),
    ];

    public static readonly BriefOption[] Tone =
    [
        new("friendly", "Friendly & warm", "Amical et chaleureux", "Thân thiện, ấm áp"),
        new("professional", "Professional", "Professionnel", "Chuyên nghiệp"),
        new("luxurious", "Luxurious", "Luxueux", "Sang trọng"),
        new("fun", "Fun & witty", "Drôle et vif", "Vui vẻ, dí dỏm"),
        new("caring", "Caring & reassuring", "Bienveillant et rassurant", "Chu đáo, an tâm"),
        new("straight", "Short & straight to the point", "Court et direct", "Ngắn gọn, đi thẳng vào ý"),
        new("local", "Proudly local", "Fièrement local", "Tự hào địa phương"),
    ];

    public static readonly BriefOption[] Copy =
    [
        new("we", "MapleKiosk writes it, we review", "MapleKiosk rédige, nous révisons", "MapleKiosk viết, chúng tôi duyệt"),
        new("together", "We give notes, you polish", "Nous donnons des notes, vous peaufinez", "Chúng tôi gửi ý, MapleKiosk hoàn thiện"),
        new("you", "We'll provide all the text", "Nous fournirons tous les textes", "Chúng tôi tự cung cấp nội dung"),
    ];

    public static IEnumerable<BriefOption> For(IEnumerable<BriefOption> options, string industry) =>
        options.Where(o => o.AppliesTo(industry));

    public static string Label(IEnumerable<BriefOption> options, string id, string culture) =>
        options.FirstOrDefault(o => o.Id == id)?.Label(culture) ?? id;

    // ---- Prompts ------------------------------------------------------------

    private static readonly Dictionary<string, (string En, string Fr, string Vi)> Text = new()
    {
        ["title"] = ("Website brief", "Brief du site Web", "Yêu cầu thiết kế website"),
        ["eyebrow"] = ("Your new website", "Votre nouveau site Web", "Website mới của bạn"),
        ["intro"] = ("Tell us how you'd like your website to look and work. There are no wrong answers — pick what feels right and skip what you're unsure about. Your designer will go through it with you.",
                     "Dites-nous à quoi votre site devrait ressembler et comment il devrait fonctionner. Il n'y a pas de mauvaise réponse — choisissez ce qui vous plaît et sautez ce qui vous semble flou. Votre designer en discutera avec vous.",
                     "Hãy cho chúng tôi biết bạn muốn website trông và hoạt động thế nào. Không có câu trả lời sai — chọn những gì bạn thấy hợp và bỏ qua những gì chưa chắc. Designer sẽ trao đổi lại với bạn."),
        ["back"] = ("← Back to setup", "← Retour à la configuration", "← Quay lại thiết lập"),
        ["pickMany"] = ("Pick as many as you like", "Choisissez-en autant que vous voulez", "Chọn bao nhiêu tùy thích"),
        ["pickTwo"] = ("Pick up to 2", "Choisissez-en jusqu'à 2", "Chọn tối đa 2"),
        ["pickOne"] = ("Pick one", "Choisissez-en un", "Chọn một"),

        ["sec.start"] = ("Starting point", "Point de départ", "Hiện trạng"),
        ["sec.goals"] = ("Goals & clients", "Objectifs et clientèle", "Mục tiêu & khách hàng"),
        ["sec.pages"] = ("Pages & features", "Pages et fonctions", "Trang & tính năng"),
        ["sec.style"] = ("Style", "Style", "Phong cách"),
        ["sec.colours"] = ("Colours", "Couleurs", "Màu sắc"),
        ["sec.examples"] = ("Websites to copy", "Sites à imiter", "Website muốn làm theo"),
        ["sec.logo"] = ("Logo, photos & assets", "Logo, photos et ressources", "Logo, hình ảnh & tài nguyên"),
        ["sec.voice"] = ("Voice & words", "Ton et textes", "Giọng văn & nội dung"),
        ["sec.timeline"] = ("Timeline & approval", "Échéancier et approbation", "Thời gian & duyệt"),

        ["mode"] = ("Light or dark?", "Clair ou sombre?", "Nền sáng hay tối?"),
        ["mode.hint"] = ("Light sites feel open and fresh; dark sites feel premium and dramatic.", "Un site clair paraît ouvert et frais; un site sombre, haut de gamme et dramatique.", "Nền sáng tạo cảm giác thoáng, tươi; nền tối sang trọng, ấn tượng."),
        ["bg"] = ("Background colour", "Couleur de fond", "Màu nền"),
        ["bg.hint"] = ("The main colour behind the text — most of the page.", "La couleur principale derrière le texte — la majeure partie de la page.", "Màu chính phía sau chữ — chiếm phần lớn trang."),
        ["accent"] = ("Accent colour", "Couleur d'accent", "Màu nhấn"),
        ["accent.hint"] = ("Used for buttons, links and highlights — the colour people remember.", "Pour les boutons, liens et points forts — la couleur dont on se souvient.", "Dùng cho nút, liên kết và điểm nhấn — màu khách sẽ nhớ."),
        ["custom"] = ("Custom…", "Personnalisée…", "Tự chọn…"),
        ["noPref"] = ("No preference", "Aucune préférence", "Không yêu cầu"),
        ["preview"] = ("Preview", "Aperçu", "Xem trước"),
        ["preview.hint"] = ("A rough idea of your choices together — your designer will refine it.", "Une idée approximative de vos choix — votre designer l'affinera.", "Hình dung sơ bộ các lựa chọn — designer sẽ tinh chỉnh thêm."),
        ["preview.cta"] = ("Book now", "Réserver", "Đặt lịch"),
        ["preview.tag"] = ("Welcome — we can't wait to see you.", "Bienvenue — au plaisir de vous voir.", "Chào mừng — rất mong được gặp bạn."),
        ["examples.intro"] = ("Share up to 3 websites whose design you'd like us to follow — from any business or country. Tell us what to copy from each.", "Partagez jusqu'à 3 sites dont vous aimeriez qu'on s'inspire — de tout secteur, tout pays. Dites-nous quoi reprendre de chacun.", "Gửi tối đa 3 website bạn muốn chúng tôi làm theo — bất kỳ ngành, quốc gia nào. Cho biết muốn lấy điểm gì từ mỗi trang."),
        ["examples.copy"] = ("What should we copy?", "Que devons-nous reprendre?", "Muốn làm theo điểm nào?"),
        ["examples.open"] = ("Open ↗", "Ouvrir ↗", "Mở ↗"),

        ["s1"] = ("Starting point", "Point de départ", "Hiện trạng"),
        ["s1.site"] = ("Do you have a website today?", "Avez-vous un site Web aujourd'hui?", "Hiện bạn đã có website chưa?"),
        ["s1.url"] = ("Current website address", "Adresse du site actuel", "Địa chỉ website hiện tại"),
        ["s1.likes"] = ("What works well on it?", "Qu'est-ce qui fonctionne bien?", "Điều gì đang ổn?"),
        ["s1.problems"] = ("What bothers you about it?", "Qu'est-ce qui vous dérange?", "Điều gì bạn chưa hài lòng?"),
        ["s1.domain"] = ("Domain name (yoursalon.com)", "Nom de domaine (votresalon.com)", "Tên miền (tentiem.com)"),
        ["s1.domainName"] = ("Domain you own or would like", "Domaine que vous possédez ou souhaitez", "Tên miền đang có hoặc mong muốn"),
        ["s1.email"] = ("We'd like email addresses on our domain", "Nous voulons des adresses courriel sur notre domaine", "Muốn có email theo tên miền"),
        ["s1.emailList"] = ("Which addresses?", "Quelles adresses?", "Những địa chỉ nào?"),

        ["s2"] = ("Goals & clients", "Objectifs et clientèle", "Mục tiêu & khách hàng"),
        ["s2.goals"] = ("What should the website do for you?", "Que doit faire le site pour vous?", "Website cần giúp bạn điều gì?"),
        ["s2.primary"] = ("If you had to pick just one, which matters most?", "S'il fallait n'en garder qu'un, lequel compte le plus?", "Nếu chỉ chọn một, điều gì quan trọng nhất?"),
        ["s2.audience"] = ("Who are your clients?", "Qui sont vos clients?", "Khách hàng của bạn là ai?"),
        ["s2.audienceNotes"] = ("Anything else about them? (age, neighbourhood, languages they speak)", "Autre chose à leur sujet? (âge, quartier, langues)", "Thêm về khách hàng? (độ tuổi, khu vực, ngôn ngữ)"),
        ["s2.difference"] = ("Why do clients choose you over the place down the street?", "Pourquoi vos clients vous choisissent-ils plutôt que le voisin?", "Vì sao khách chọn bạn thay vì tiệm bên cạnh?"),
        ["s2.words"] = ("Describe your business in three words", "Décrivez votre commerce en trois mots", "Mô tả cửa hàng bằng ba từ"),

        ["s3"] = ("Pages & features", "Pages et fonctions", "Trang & tính năng"),
        ["s3.pages"] = ("Which pages do you want?", "Quelles pages voulez-vous?", "Bạn muốn có những trang nào?"),
        ["s3.features"] = ("Which features?", "Quelles fonctions?", "Những tính năng nào?"),
        ["s3.languages"] = ("Website languages", "Langues du site", "Ngôn ngữ của website"),
        ["s3.notes"] = ("Anything else the site must do?", "Autre chose que le site doit faire?", "Website cần làm gì thêm?"),

        ["s4"] = ("Look & feel", "Style visuel", "Phong cách"),
        ["s4.styles"] = ("Which style feels most like you?", "Quel style vous ressemble le plus?", "Phong cách nào giống bạn nhất?"),
        ["s4.sliders"] = ("Fine-tune the feeling", "Affinez l'ambiance", "Tinh chỉnh cảm giác"),
        ["s4.palette"] = ("Colours", "Couleurs", "Màu sắc"),
        ["s4.brand"] = ("Your brand colours (names or codes)", "Vos couleurs de marque (noms ou codes)", "Màu thương hiệu (tên hoặc mã màu)"),
        ["s4.avoid"] = ("Colours to avoid", "Couleurs à éviter", "Màu nên tránh"),
        ["s4.fonts"] = ("Lettering", "Typographie", "Kiểu chữ"),

        ["s5"] = ("Logo & photos", "Logo et photos", "Logo & hình ảnh"),
        ["s5.logo"] = ("Your logo", "Votre logo", "Logo của bạn"),
        ["s5.photos"] = ("Photos for the site", "Photos pour le site", "Hình ảnh cho website"),
        ["s5.mood"] = ("What kind of pictures?", "Quel genre d'images?", "Kiểu hình ảnh nào?"),
        ["assets"] = ("Which web assets can you share with us?", "Quelles ressources pouvez-vous nous fournir?", "Bạn có thể gửi những tài nguyên nào?"),
        ["assets.fonts"] = ("Font names you use (if any)", "Noms des polices utilisées (s'il y a lieu)", "Tên font chữ đang dùng (nếu có)"),
        ["assets.link"] = ("Link to a shared folder (Google Drive, Dropbox, OneDrive)", "Lien vers un dossier partagé (Google Drive, Dropbox, OneDrive)", "Link thư mục chia sẻ (Google Drive, Dropbox, OneDrive)"),
        ["assets.linkHint"] = ("Best for many photos, videos or big files. Please allow anyone with the link to view.", "Idéal pour beaucoup de photos, des vidéos ou de gros fichiers. Autorisez l'accès à toute personne ayant le lien.", "Phù hợp khi có nhiều ảnh, video hoặc file lớn. Vui lòng cho phép “bất kỳ ai có link” được xem."),
        ["s5.upload"] = ("Upload images, fonts and brand files", "Téléversez images, polices et fichiers de marque", "Tải lên hình ảnh, font chữ và file thương hiệu"),
        ["s5.uploadHint"] = ("Images, fonts (TTF/OTF/WOFF), PDF, AI/EPS/SVG, short videos or a ZIP · up to 25 MB each", "Images, polices (TTF/OTF/WOFF), PDF, AI/EPS/SVG, courtes vidéos ou ZIP · 25 Mo max. chacun", "Ảnh, font (TTF/OTF/WOFF), PDF, AI/EPS/SVG, video ngắn hoặc ZIP · tối đa 25 MB mỗi file"),

        ["s6"] = ("Voice & words", "Ton et textes", "Giọng văn & nội dung"),
        ["s6.tone"] = ("How should the website sound?", "Quel ton pour le site?", "Website nên “nói chuyện” thế nào?"),
        ["s6.copy"] = ("Who writes the text?", "Qui rédige les textes?", "Ai viết nội dung?"),
        ["s6.messages"] = ("Key messages, slogans or facts we must include", "Messages clés, slogans ou faits à inclure", "Thông điệp, slogan hoặc thông tin bắt buộc có"),
        ["s6.messagesPh"] = ("e.g. family-owned since 2009, hospital-grade sterilisation, free parking", "ex. entreprise familiale depuis 2009, stérilisation de qualité hospitalière, stationnement gratuit", "vd: gia đình làm từ 2009, tiệt trùng chuẩn y tế, có chỗ đậu xe miễn phí"),

        ["s7"] = ("Inspiration", "Inspiration", "Cảm hứng"),
        ["s7.refs"] = ("Websites you like (any business, any country)", "Sites que vous aimez (tout secteur, tout pays)", "Website bạn thích (bất kỳ ngành, quốc gia nào)"),
        ["s7.refsHint"] = ("The more you tell us why, the better we understand your taste.", "Plus vous expliquez pourquoi, mieux nous comprenons vos goûts.", "Càng giải thích lý do, chúng tôi càng hiểu gu của bạn."),
        ["s7.url"] = ("Website address", "Adresse du site", "Địa chỉ website"),
        ["s7.likes"] = ("What do you like?", "Qu'aimez-vous?", "Bạn thích điểm gì?"),
        ["s7.dislikes"] = ("Anything you don't like?", "Quelque chose que vous n'aimez pas?", "Điểm nào không thích?"),
        ["s7.competitors"] = ("Main competitors (names or websites)", "Principaux concurrents (noms ou sites)", "Đối thủ chính (tên hoặc website)"),
        ["s7.must"] = ("Must-haves", "Incontournables", "Bắt buộc phải có"),
        ["s7.avoid"] = ("Please avoid…", "À éviter…", "Xin tránh…"),

        ["s8"] = ("Timeline & approval", "Échéancier et approbation", "Thời gian & duyệt"),
        ["s8.date"] = ("Ideal launch date", "Date de lancement idéale", "Ngày muốn ra mắt"),
        ["s8.reason"] = ("Is there a reason for that date? (opening, season, event)", "Une raison pour cette date? (ouverture, saison, événement)", "Lý do chọn ngày này? (khai trương, mùa, sự kiện)"),
        ["s8.approver"] = ("Who approves the design?", "Qui approuve le design?", "Ai duyệt thiết kế?"),
        ["s8.approverContact"] = ("Their phone or email", "Son téléphone ou courriel", "Số điện thoại hoặc email"),
        ["s8.notes"] = ("Anything else for your designer?", "Autre chose pour votre designer?", "Còn gì muốn nhắn designer?"),

        ["send"] = ("Send brief to our designer", "Envoyer le brief au designer", "Gửi yêu cầu cho designer"),
        ["resend"] = ("Send updates to our designer", "Envoyer les mises à jour", "Gửi cập nhật cho designer"),
        ["sending"] = ("Sending…", "Envoi…", "Đang gửi…"),
        ["sent"] = ("Sent to our design team on {0}. You can keep editing — changes save automatically; press the button again to let them know.",
                    "Envoyé à notre équipe de design le {0}. Vous pouvez continuer à modifier — les changements sont enregistrés; appuyez de nouveau sur le bouton pour les prévenir.",
                    "Đã gửi cho đội thiết kế ngày {0}. Bạn vẫn có thể chỉnh sửa — thay đổi được lưu tự động; bấm nút lần nữa để báo cho họ."),
        ["notIncluded"] = ("A website isn't part of your current plan. Contact us if you'd like one.", "Un site Web ne fait pas partie de votre forfait. Contactez-nous si vous en voulez un.", "Gói hiện tại chưa bao gồm website. Liên hệ chúng tôi nếu bạn cần."),
        ["card.title"] = ("Your website", "Votre site Web", "Website của bạn"),
        ["card.body"] = ("Your plan includes a website. Tell us the style, colours, pages and features you want — it takes about 15 minutes.", "Votre forfait comprend un site Web. Dites-nous le style, les couleurs, les pages et les fonctions souhaités — environ 15 minutes.", "Gói của bạn có website. Hãy cho chúng tôi biết phong cách, màu sắc, các trang và tính năng bạn muốn — khoảng 15 phút."),
        ["card.start"] = ("Start the website brief →", "Commencer le brief →", "Bắt đầu →"),
        ["card.continue"] = ("Continue the website brief →", "Continuer le brief →", "Tiếp tục →"),
        ["card.done"] = ("Brief sent ✓ — edit →", "Brief envoyé ✓ — modifier →", "Đã gửi ✓ — chỉnh sửa →"),
    };

    public static string T(string key, string culture) =>
        Text.TryGetValue(key, out var t) ? culture switch { "fr" => t.Fr, "vi" => t.Vi, _ => t.En } : key;
}
