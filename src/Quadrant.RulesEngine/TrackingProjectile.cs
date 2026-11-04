namespace Quadrant.RulesEngine;

/// <summary>A team-owned, automatically moving tracking weapon in flight.</summary>
public sealed class TrackingProjectile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int TeamId { get; init; }
    public required string OwnerShipId { get; init; }
    public required string TargetShipId { get; init; }
    public required int Speed { get; init; }
    public required int GunnerBonus { get; init; }
    public required Weapon Weapon { get; init; }
    public Hex Position { get; set; }
    public Facing Facing { get; set; }

}
