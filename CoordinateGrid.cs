using Godot;
using System;
using System.Collections.Generic;

public partial class CoordinateGrid : Node2D
{
    // region ПРИВЯЗКИ
    [ExportGroup("Привязки")]
    [Export] private Control _targetBackground;
    // endregion

    [ExportGroup("Настройки Шрифта")]
    [Export] public Font LabelFont { get; set; }
    [Export] public int FontSize { get; set; } = 24;

    [ExportGroup("Реальные размеры (мм)")]
    [Export] public float RealWorldWidthMM { get; set; } = 1600f;
    [Export] public float RealWorldHeightMM { get; set; } = 900f;

    [ExportGroup("Стиль")]
    [Export] public Color MajorLineColor { get; set; } = new Color(0.2f, 0.4f, 0.8f, 0.5f);
    [Export] public Color MinorLineColor { get; set; } = new Color(0.2f, 0.4f, 0.8f, 0.2f);
    // НОВОЕ: Цвета для детали
    [Export] public Color PieceBorderColor { get; set; } = new Color(0.0f, 0.8f, 1.0f, 0.8f); // Яркий циан
    [Export] public Color PieceFillColor { get; set; } = new Color(0.0f, 0.8f, 1.0f, 0.1f);   // Полупрозрачный циан

    // Данные траектории
    private List<Vector2> _points = new List<Vector2>();
    private List<Color> _pointColors = new List<Color>();

    // НОВОЕ: Данные детали
    private bool _hasPiece = false;
    private Vector2 _pieceSizeMM; // X = Width, Y = Height

    public Rect2 GridArea => GetCurrentArea();

    public float PixelsPerMM_X => GridArea.Size.X / Math.Max(1, RealWorldWidthMM);
    public float PixelsPerMM_Y => GridArea.Size.Y / Math.Max(1, RealWorldHeightMM);

    public override void _Ready()
    {
        ZIndex = 1;

        if (_targetBackground == null)
        {
            _targetBackground = GetParent().GetNodeOrNull<Control>("BlackPanel");
            if (_targetBackground == null && GetParent().GetParent() != null)
            {
                _targetBackground = GetParent().GetParent().GetNodeOrNull<Control>("BlackPanel");
            }
        }

        if (_targetBackground == null)
            GD.PrintErr("[CoordinateGrid] BlackPanel не найден! Проверь привязку.");
        else
            _targetBackground.Resized += QueueRedraw;

        CallDeferred(CanvasItem.MethodName.QueueRedraw);
    }

    private Rect2 GetCurrentArea()
    {
        if (_targetBackground != null && _targetBackground.IsInsideTree())
        {
            return new Rect2(Vector2.Zero, _targetBackground.Size);
        }
        return new Rect2(0, 0, RealWorldWidthMM, RealWorldHeightMM);
    }

    public void UpdatePoints(IEnumerable<Vector2> positions, IEnumerable<Color> colors)
    {
        _points.Clear();
        _pointColors.Clear();
        if (positions != null) _points.AddRange(positions);
        if (colors != null) _pointColors.AddRange(colors);
        while (_pointColors.Count < _points.Count) _pointColors.Add(Colors.White);
        QueueRedraw();
    }

    // --- НОВЫЙ МЕТОД: Установка прямоугольника детали ---
    public void SetPieceRectangle(float widthMM, float heightMM)
    {
        if (widthMM <= 0 || heightMM <= 0)
        {
            _hasPiece = false;
        }
        else
        {
            _hasPiece = true;
            _pieceSizeMM = new Vector2(widthMM, heightMM);
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        Rect2 area = GridArea;
        if (area.Size.X < 10 || area.Size.Y < 10) return;

        Font fontToUse = LabelFont ?? ThemeDB.FallbackFont;

        // 1. Сетка
        DrawGridLines(area, fontToUse);

        // 2. Деталь (синий прямоугольник)
        DrawPiece(area);

        // 3. НОВОЕ: Траектория (линии между точками)
        DrawTrajectory(area);

        // 4. Сами точки (крестики/маркеры)
        DrawPointMarkers(area, fontToUse);
    }

    // --- НОВЫЙ МЕТОД: Рисует линии от точки к точке ---
    private void DrawTrajectory(Rect2 area)
    {
        if (_points == null || _points.Count < 2) return;

        float scaleX = PixelsPerMM_X;
        float scaleY = PixelsPerMM_Y;

        // Проходим по списку и соединяем i с i+1
        for (int i = 0; i < _points.Count - 1; i++)
        {
            // Переводим мм в пиксели для точки А
            float x1 = area.Position.X + _points[i].X * scaleX;
            float y1 = area.Position.Y + (RealWorldHeightMM - _points[i].Y) * scaleY;
            Vector2 p1 = new Vector2(x1, y1);

            // Переводим мм в пиксели для точки Б
            float x2 = area.Position.X + _points[i + 1].X * scaleX;
            float y2 = area.Position.Y + (RealWorldHeightMM - _points[i + 1].Y) * scaleY;
            Vector2 p2 = new Vector2(x2, y2);

            // Рисуем жирную линию
            // Цвет берем от точки назначения, или дефолтный желтый
            Color lineColor = (i + 1 < _pointColors.Count) ? _pointColors[i + 1] : Colors.Yellow;

            // Делаем линию полупрозрачной, чтобы не перекрывала всё
            lineColor.A = 0.6f;

            DrawLine(p1, p2, lineColor, 2.0f, true); // Толщина 2 пикселя
        }
    }

    // --- НОВЫЙ МЕТОД: Логика отрисовки детали ---
    private void DrawPiece(Rect2 area)
    {
        if (!_hasPiece) return;

        float scaleX = PixelsPerMM_X;
        float scaleY = PixelsPerMM_Y;

        // 1. Находим центр рабочего поля в мм
        float centerMmX = RealWorldWidthMM / 2.0f;
        float centerMmY = RealWorldHeightMM / 2.0f;

        // 2. Вычисляем левый нижний угол детали (в мм)
        // (Помним, что в мире ЧПУ Y растет вверх, поэтому "нижний" угол имеет меньший Y)
        float pieceMinMmX = centerMmX - (_pieceSizeMM.X / 2.0f);
        float pieceMinMmY = centerMmY - (_pieceSizeMM.Y / 2.0f);

        // 3. Переводим в пиксели экрана
        // Важно: для Y используем инверсию, т.к. на экране Y растет вниз
        float pixelX = area.Position.X + pieceMinMmX * scaleX;
        // Верхняя граница на экране соответствует максимальному Y в мире (center + height/2)
        float pixelY_TopEdge = area.Position.Y + (RealWorldHeightMM - (centerMmY + _pieceSizeMM.Y / 2.0f)) * scaleY;

        float pixelWidth = _pieceSizeMM.X * scaleX;
        float pixelHeight = _pieceSizeMM.Y * scaleY;

        Rect2 pieceRectPixel = new Rect2(pixelX, pixelY_TopEdge, pixelWidth, pixelHeight);

        // Рисуем заливку
        DrawRect(pieceRectPixel, PieceFillColor, true);
        // Рисуем рамку (толщиной 3 пикселя)
        DrawRect(pieceRectPixel, PieceBorderColor, false, 3.0f);
    }
    // -------------------------------------------

    private void DrawGridLines(Rect2 area, Font font)
    {
        float scaleX = PixelsPerMM_X;
        float scaleY = PixelsPerMM_Y;

        for (float mm = 0; mm <= RealWorldWidthMM; mm += 50)
        {
            bool major = Mathf.IsEqualApprox(mm % 100, 0);
            float x = area.Position.X + mm * scaleX;
            if (x > area.End.X) break;

            DrawLine(new Vector2(x, area.Position.Y), new Vector2(x, area.End.Y),
                major ? MajorLineColor : MinorLineColor, major ? 2 : 1);

            if (major && mm > 0 && font != null)
                DrawString(font, new Vector2(x + 2, area.End.Y - 5), $"{mm:F0}", fontSize: 16);
        }

        for (float mm = 0; mm <= RealWorldHeightMM; mm += 50)
        {
            bool major = Mathf.IsEqualApprox(mm % 100, 0);
            float invertedMM = RealWorldHeightMM - mm;
            float y = area.Position.Y + mm * scaleY;
            if (y > area.End.Y) break;

            DrawLine(new Vector2(area.Position.X, y), new Vector2(area.End.X, y),
                major ? MajorLineColor : MinorLineColor, major ? 2 : 1);

            if (major && invertedMM > 0 && font != null)
                DrawString(font, new Vector2(area.Position.X + 5, y - 2), $"{invertedMM:F0}", fontSize: 16);
        }
    }

    private void DrawPointMarkers(Rect2 area, Font font)
    {
        if (_points == null) return;
        float scaleX = PixelsPerMM_X;
        float scaleY = PixelsPerMM_Y;

        for (int i = 0; i < _points.Count; i++)
        {
            float x = area.Position.X + _points[i].X * scaleX;
            float yReal = RealWorldHeightMM - _points[i].Y;
            float y = area.Position.Y + yReal * scaleY;
            Color c = _pointColors[i];

            DrawLine(new Vector2(x, area.Position.Y), new Vector2(x, area.End.Y), c, 1);
            DrawLine(new Vector2(area.Position.X, y), new Vector2(area.End.X, y), c, 1);
            DrawCircle(new Vector2(x, y), 4f, c);

            if (font != null)
            {
                string label = $"{_points[i].X:0},{_points[i].Y:0}";
                DrawString(font, new Vector2(x + 5, y - 5), label, fontSize: FontSize, modulate: c);
            }
        }
    }
}