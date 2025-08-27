using Godot;
using System;

public partial class BlackPanel : ColorRect
{
    [Export] private CoordinateGrid _grid;

    public override void _Ready()
    {
        this.ItemRectChanged += () => _grid?.UpdateGridSize(Size);
        ZIndex = 1;
    }
}
