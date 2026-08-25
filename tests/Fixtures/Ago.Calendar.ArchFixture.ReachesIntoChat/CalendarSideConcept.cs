using Ago.Chat.ArchFixture;

namespace Ago.Calendar.ArchFixture.ReachesIntoChat;

/// <summary>
/// A Calendar-side type that borrows a Chat-side one - exactly the mistake `adr/0027` forbids,
/// written the way it would actually be written rather than described.
/// </summary>
public sealed class CalendarSideConcept
{
    public string Describe(ChatSideConcept borrowed) => borrowed.Name;
}
