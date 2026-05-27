using System.Collections.Frozen;

namespace Hook.Features.Ai.PlatformQa;

internal static class IdentityPhraseBook
{
    internal const string FallbackEn =
        "I'm Hook — a WhatsApp bot that connects you with nearby service providers, " +
        "or lists you as a provider so clients can find you. Reply REQUEST if you need " +
        "a service, or REGISTER to offer one. The platform is free during launch.";

    internal static readonly FrozenDictionary<string, string> Phrases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // English
            ["what is this"] = "en",
            ["what's this"] = "en",
            ["whats this"] = "en",
            ["what is hook"] = "en",
            ["what's hook"] = "en",
            ["whats hook"] = "en",
            ["what are you"] = "en",
            ["what're you"] = "en",
            ["what r u"] = "en",
            ["who are you"] = "en",
            ["who r u"] = "en",
            ["what is your name"] = "en",
            ["what's your name"] = "en",
            ["whats your name"] = "en",
            ["your name"] = "en",
            // French
            ["qui es tu"] = "fr",
            ["qui es-tu"] = "fr",
            ["qui êtes vous"] = "fr",
            ["qui êtes-vous"] = "fr",
            ["c'est quoi"] = "fr",
            ["c'est quoi ça"] = "fr",
            ["c'est quoi hook"] = "fr",
            ["quel est ton nom"] = "fr",
            // Arabic
            ["من انت"] = "ar",
            ["ما هذا"] = "ar",
            ["ما اسمك"] = "ar",
            // Wolof
            ["yan nga tudd"] = "wo",
            ["loolu lan la"] = "wo",
            // Spanish
            ["que es esto"] = "es",
            ["qué es esto"] = "es",
            ["quien eres"] = "es",
            ["quién eres"] = "es",
            ["como te llamas"] = "es",
            ["cómo te llamas"] = "es",
            // Portuguese
            ["o que e isto"] = "pt",
            ["o que é isto"] = "pt",
            ["quem es voce"] = "pt",
            ["quem és você"] = "pt",
            ["qual e o seu nome"] = "pt",
            ["qual é o seu nome"] = "pt",
            // Fula (Pulaar). TODO: native-speaker review of phrasing + reply text.
            ["ko honɗun"] = "ff",
            ["ko honɗun woni ɗoo"] = "ff",
            // Mandinka. TODO: native-speaker review of phrasing + reply text.
            ["muna le ñin"] = "mnk",
            ["i too le"] = "mnk",
        }.ToFrozenDictionary();

    internal static readonly FrozenDictionary<string, string> LocalisedReplies =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fr"] =
                "Je suis Hook — un bot WhatsApp qui vous met en relation avec des prestataires " +
                "de services à proximité, ou vous inscrit comme prestataire pour que des clients " +
                "vous trouvent. Répondez REQUEST si vous cherchez un service, ou REGISTER pour en " +
                "proposer un. La plateforme est gratuite pendant le lancement.",
            ["ar"] =
                "أنا Hook — روبوت واتساب يربطك بمزودي الخدمات القريبين، أو يدرجك كمزود ليتمكن " +
                "العملاء من العثور عليك. أرسل REQUEST إذا كنت تحتاج خدمة، أو REGISTER لتقديم " +
                "خدمة. المنصة مجانية خلال فترة الإطلاق.",
            ["wo"] =
                "Maa di Hook — bot bu WhatsApp bu lay jokkale ak ñi koy joxe service ci sa wàll, " +
                "walla bu lay bind ndax sa kiliyaan yi gis la. Bind REQUEST su nga soxla service, " +
                "walla REGISTER ngir joxe benn. Plateforme bi neexul dara nag bi nu di tàmbali.",
            ["es"] =
                "Soy Hook — un bot de WhatsApp que te conecta con prestadores de servicios " +
                "cercanos, o te lista como prestador para que clientes te encuentren. Responde " +
                "REQUEST si necesitas un servicio, o REGISTER para ofrecer uno. La plataforma " +
                "es gratuita durante el lanzamiento.",
            ["pt"] =
                "Sou o Hook — um bot do WhatsApp que te conecta com prestadores de serviços " +
                "próximos, ou te lista como prestador para que os clientes te encontrem. " +
                "Responde REQUEST se precisas de um serviço, ou REGISTER para oferecer um. " +
                "A plataforma é gratuita durante o lançamento.",
            // TODO: native-speaker translations for ff / mnk. English fallback for now.
            ["ff"] = FallbackEn,
            ["mnk"] = FallbackEn,
        }.ToFrozenDictionary();
}
