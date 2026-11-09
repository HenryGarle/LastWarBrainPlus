namespace LastWar.Client;

/// <summary>What the chat gateway needs: the player uid and the token from the game init push.</summary>
public sealed record ChatCredentials(string Uid, string ChatToken);
