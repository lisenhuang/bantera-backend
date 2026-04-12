namespace BanteraApi.Videos;

public record SaveCueRequest(Guid VideoId, string CueId, int CueIndex);
