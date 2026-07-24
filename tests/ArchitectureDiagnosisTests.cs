namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the architecture-based diagnosis of a failed VF-point read: pre-Pascal cards
/// have no curve interface to port, and are told so by family name.</summary>
public class ArchitectureDiagnosisTests
{
    [Theory]
    [InlineData(0x110u, "Maxwell")] // GM000
    [InlineData(0x120u, "Maxwell")] // GM200
    [InlineData(0xE0u, "Kepler")]   // GK100
    [InlineData(0x100u, "Kepler")]  // GK200
    [InlineData(0xC0u, "Fermi")]    // GF100
    [InlineData(0x80u, "architecture 0x80")]
    public void PrePascal_NamesTheFamilyAsUntunable(uint architectureId, string family)
    {
        string? diagnosis = NvApi.CurveUnavailableDiagnosis(architectureId);
        Assert.NotNull(diagnosis);
        Assert.Contains(family, diagnosis);
        Assert.Contains("introduced with Pascal", diagnosis);
    }

    [Theory]
    [InlineData(0x130u)] // GP100 Pascal
    [InlineData(0x160u)] // TU100 Turing
    [InlineData(0x1B0u)] // GB200 Blackwell
    public void PascalOnward_ReportsAStubbedDriverInterface(uint architectureId)
    {
        string? diagnosis = NvApi.CurveUnavailableDiagnosis(architectureId);
        Assert.NotNull(diagnosis);
        Assert.Contains("doesn't expose", diagnosis);
    }

    [Fact]
    public void UnknownArchitecture_LeavesTheDriverErrorAlone()
    {
        Assert.Null(NvApi.CurveUnavailableDiagnosis(null));
    }
}
