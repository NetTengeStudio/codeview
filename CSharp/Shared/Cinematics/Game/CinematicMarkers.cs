using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public abstract class ScpCineMarker : ItemComponent {
        internal static readonly List<ScpCineMarker> All = new List<ScpCineMarker>();
        internal static uint Revision { get; private set; } = 1;
        internal readonly CinematicMarkerKind MarkerKind;
        protected ScpCineMarker(Item item, ContentXElement element, CinematicMarkerKind kind) : base(item, element) {
            MarkerKind = kind; IsActive = false;
            All.Add(this); Revision++;
        }
        internal Item Owner => item;
        [Serialize("0,0", IsPropertySaveable.Yes)]
        public Vector2 SubjectOffset { get; set; }
        public override void OnItemLoaded() { base.OnItemLoaded(); item.HiddenInGame = true; Revision++; }
        public override void RemoveComponentSpecific() { base.RemoveComponentSpecific(); All.Remove(this); Revision++; }
        internal CinematicMarkerRecord Record() {
            var record = new CinematicMarkerRecord { Id = item.ID, Kind = MarkerKind, Position = new Vec2(item.Position.X, item.Position.Y), SubjectOffset = new Vec2(SubjectOffset.X, SubjectOffset.Y) };
            int count = 0;
            foreach (MapEntity linked in item.linkedTo) if (linked is Item && !linked.Removed) count++;
            record.Links = new ushort[count];
            count = 0;
            foreach (MapEntity linked in item.linkedTo) if (linked is Item && !linked.Removed) record.Links[count++] = linked.ID;
            Fill(record);
            return record;
        }
        internal Item Subject() {
            foreach (MapEntity linked in item.linkedTo) if (linked is Item other && !other.Removed && other.GetComponent<ScpCineMarker>() == null) return other;
            return null;
        }
        internal void CaptureSubjectOffset() {
            if (Subject() is Item subject) SubjectOffset = item.Position - subject.Position;
        }
        protected abstract void Fill(CinematicMarkerRecord record);
    }

    public sealed class ScpCineScene : ScpCineMarker {
        public ScpCineScene(Item item, ContentXElement element) : base(item, element, CinematicMarkerKind.Scene) { }
        [Editable(MaxLength = 64), Serialize("arrival", IsPropertySaveable.Yes, description: "Уникальное имя сцены в подлодке: латиница, цифры, '_', '.', '-'")]
        public string SceneName { get; set; }
        [Editable, Serialize(ЗапускСцены.НачалоРаунда, IsPropertySaveable.Yes, description: "Когда начинается сцена")]
        public ЗапускСцены Trigger { get; set; }
        [Editable(MaxLength = 64), Serialize("", IsPropertySaveable.Yes, description: "Сигнал, который запускает сцену (для запуска по сигналу)")]
        public string TriggerSignal { get; set; }
        [Editable(10f, 3600f, 0), Serialize(600f, IsPropertySaveable.Yes, description: "Предельная длительность всей сцены, секунды")]
        public float MaxSeconds { get; set; }
        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Посадить игроков в связанный вертолёт перед началом сцены")]
        public bool BoardPassengers { get; set; }
        [Editable(MaxLength = 64), Serialize("", IsPropertySaveable.Yes, description: "Места пассажиров через запятую; пусто — все пассажирские места вертолёта")]
        public string PassengerSeats { get; set; }
        [Editable(0f, 300f, 0), Serialize(80f, IsPropertySaveable.Yes, description: "Масса одного пассажира для предпросмотра, кг")]
        public float PreviewCrewMassKg { get; set; }
        protected override void Fill(CinematicMarkerRecord record) => record.Scene = new CinematicSceneSettings {
            Name = SceneName ?? "", Trigger = (CinematicSceneTrigger)Trigger, TriggerSignal = TriggerSignal ?? "", MaxSeconds = MaxSeconds,
            BoardPassengers = BoardPassengers, PassengerSeats = PassengerSeats ?? "", PreviewCrewMassKg = PreviewCrewMassKg
        };
    }

    public sealed class ScpCineShot : ScpCineMarker {
        public ScpCineShot(Item item, ContentXElement element) : base(item, element, CinematicMarkerKind.Shot) { }
        [Editable(MaxLength = 64), Serialize("", IsPropertySaveable.Yes, description: "Имя кадра; пусто — имя по номеру предмета")]
        public string ShotName { get; set; }
        [Editable(0f, 3600f, 1), Serialize(0f, IsPropertySaveable.Yes, description: "Кадр длится не меньше, секунды")]
        public float MinSeconds { get; set; }
        [Editable(0.1f, 3600f, 1), Serialize(60f, IsPropertySaveable.Yes, description: "Кадр длится не больше, секунды; затем переход «При превышении»")]
        public float MaxSeconds { get; set; }
        [Editable, Serialize(ПереходКадра.ПоВремени, IsPropertySaveable.Yes, description: "Условие перехода к следующему кадру")]
        public ПереходКадра Advance { get; set; }
        [Editable(MaxLength = 64), Serialize("", IsPropertySaveable.Yes, description: "Имя путевой точки или сигнала для условия")]
        public string AdvanceValue { get; set; }
        [Editable(MaxLength = 64), Serialize("", IsPropertySaveable.Yes, description: "Следующий кадр; пусто — связанный кадр цепочки, end — конец сцены")]
        public string NextShot { get; set; }
        [Editable, Serialize(ВетвьКадра.Нет, IsPropertySaveable.Yes, description: "Дополнительное условие, уводящее в другой кадр")]
        public ВетвьКадра Branch { get; set; }
        [Editable(MaxLength = 64), Serialize("", IsPropertySaveable.Yes, description: "Имя путевой точки или сигнала для ветки")]
        public string BranchValue { get; set; }
        [Editable(MaxLength = 64), Serialize("", IsPropertySaveable.Yes, description: "Кадр, в который ведёт ветка")]
        public string BranchShot { get; set; }
        [Editable(MaxLength = 64), Serialize("abort", IsPropertySaveable.Yes, description: "Куда перейти при превышении длительности: next, end, abort или имя кадра")]
        public string TimeoutShot { get; set; }
        protected override void Fill(CinematicMarkerRecord record) => record.Shot = new CinematicShotSettings {
            Name = ShotName ?? "", MinSeconds = MinSeconds, MaxSeconds = MaxSeconds, Advance = (CinematicShotAdvance)Advance, AdvanceValue = AdvanceValue ?? "",
            Next = NextShot ?? "", Branch = (CinematicShotBranch)Branch, BranchValue = BranchValue ?? "", BranchTo = BranchShot ?? "", TimeoutTo = TimeoutShot ?? "abort"
        };
    }

    public sealed class ScpCineCamera : ScpCineMarker {
        public ScpCineCamera(Item item, ContentXElement element) : base(item, element, CinematicMarkerKind.Camera) { }
        [Editable, Serialize(ВидКамеры.Неподвижная, IsPropertySaveable.Yes, description: "Неподвижная, проезд к связанной второй камере или следящая за вертолётом")]
        public ВидКамеры CameraKind { get; set; }
        [Editable(0f, 3600f, 1), Serialize(0f, IsPropertySaveable.Yes, description: "Начало ракурса от начала кадра, секунды")]
        public float StartAt { get; set; }
        [Editable(0.1f, 3600f, 1), Serialize(8f, IsPropertySaveable.Yes, description: "Длительность проезда, секунды")]
        public float Duration { get; set; }
        [Editable(0.1f, 2f, 2), Serialize(1f, IsPropertySaveable.Yes, description: "Масштаб кадра")]
        public float Zoom { get; set; }
        [Editable(0.01f, 30f, 2), Serialize(2f, IsPropertySaveable.Yes, description: "Время мягкого перехода от предыдущего ракурса, секунды")]
        public float Blend { get; set; }
        [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Резкая склейка вместо перехода")]
        public bool Cut { get; set; }
        [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Сохранить скорость при склейке по движению")]
        public bool MatchVelocity { get; set; }
        [Editable(0f, 1f, 2), Serialize(0f, IsPropertySaveable.Yes, description: "Импульс камеры в начале ракурса: отрыв, пролёт или касание")]
        public float Impulse { get; set; }
        [Editable(0f, 10f, 2), Serialize(0.2f, IsPropertySaveable.Yes, description: "Сила тряски от пролёта и потока ротора")]
        public float Shake { get; set; }
        [Editable(0.1f, 0.9f, 2), Serialize(0.5f, IsPropertySaveable.Yes, description: "Положение вертолёта в кадре по горизонтали (следящая)")]
        public float FrameX { get; set; }
        [Editable(0.1f, 0.9f, 2), Serialize(0.55f, IsPropertySaveable.Yes, description: "Положение вертолёта в кадре по вертикали (следящая)")]
        public float FrameY { get; set; }
        [Editable(0f, 5f, 2), Serialize(0.6f, IsPropertySaveable.Yes, description: "Упреждение по скорости, секунды (следящая)")]
        public float LookAhead { get; set; }
        protected override void Fill(CinematicMarkerRecord record) => record.Camera = new CinematicCameraSettings {
            Kind = (CinematicCameraKind)CameraKind, StartAt = StartAt, Duration = Duration, Zoom = Zoom, Blend = Blend, Cut = Cut, MatchVelocity = MatchVelocity, Impulse = Impulse, Shake = Shake, FrameX = FrameX, FrameY = FrameY, LookAhead = LookAhead
        };
    }

    public sealed class ScpCineWaypoint : ScpCineMarker {
        public ScpCineWaypoint(Item item, ContentXElement element) : base(item, element, CinematicMarkerKind.Waypoint) { }
        [Editable(MaxLength = 64), Serialize("", IsPropertySaveable.Yes, description: "Имя точки для условий кадров")]
        public string PointName { get; set; }
        [Editable, Serialize(ВидТочки.Пролёт, IsPropertySaveable.Yes, description: "Пролёт с сохранением скорости, зависание или посадка (только последняя точка)")]
        public ВидТочки Mode { get; set; }
        [Editable(0.1f, 150f, 1), Serialize(25f, IsPropertySaveable.Yes, description: "Наибольшая скорость на подходе к точке, м/с")]
        public new float Speed { get; set; }
        [Editable(0f, 150f, 1), Serialize(0f, IsPropertySaveable.Yes, description: "Скорость прохождения точки, м/с (0 — по траектории)")]
        public float PassSpeed { get; set; }
        [Editable(0.05f, 10f, 2), Serialize(1.5f, IsPropertySaveable.Yes, description: "Наибольшее ускорение, м/с²; ограничено наклоном несущего винта")]
        public float Acceleration { get; set; }
        [Editable(0.05f, 20f, 2), Serialize(1f, IsPropertySaveable.Yes, description: "Плавность изменения ускорения, м/с³")]
        public float Jerk { get; set; }
        [Editable(0.05f, 100f, 2), Serialize(4f, IsPropertySaveable.Yes, description: "Радиус достижения точки, м")]
        public float Radius { get; set; }
        [Editable(0f, 600f, 1), Serialize(0f, IsPropertySaveable.Yes, description: "Выдержка в зависании, секунды")]
        public float Hold { get; set; }
        [Editable, Serialize(КурсВертолёта.Авто, IsPropertySaveable.Yes, description: "Куда смотрит нос вертолёта у точки")]
        public КурсВертолёта Heading { get; set; }
        [Editable(0.05f, 2f, 2), Serialize(0.35f, IsPropertySaveable.Yes, description: "Вертикальная скорость касания, м/с")]
        public float LandingRate { get; set; }
        [Editable(0.2f, 10f, 1), Serialize(2f, IsPropertySaveable.Yes, description: "Сколько секунд вертолёт должен стоять на шасси, чтобы посадка считалась завершённой")]
        public float SettleSeconds { get; set; }
        [Editable(0f, 25f, 1), Serialize(10f, IsPropertySaveable.Yes, description: "Наибольший уклон площадки, градусы")]
        public float MaxSlopeDegrees { get; set; }
        [Editable(0.5f, 50f, 1), Serialize(5f, IsPropertySaveable.Yes, description: "Высота над площадкой, с которой начинается снижение, м")]
        public float Clearance { get; set; }
        protected override void Fill(CinematicMarkerRecord record) => record.Waypoint = new CinematicWaypointSettings {
            Name = PointName ?? "", Kind = (CinematicWaypointKind)Mode, Speed = Speed, PassSpeed = PassSpeed, Acceleration = Acceleration, Jerk = Jerk, Radius = Radius, Hold = Hold,
            Heading = (CinematicHeadingKind)Heading, LandingRate = LandingRate, SettleSeconds = SettleSeconds, MaxSlopeDegrees = MaxSlopeDegrees, Clearance = Clearance
        };
    }

    public sealed class ScpCineActor : ScpCineMarker {
        public ScpCineActor(Item item, ContentXElement element) : base(item, element, CinematicMarkerKind.Actor) { }
        [Editable, Serialize(ДействиеЭкипажа.Высадка, IsPropertySaveable.Yes, description: "Что делает экипаж; точка назначения — сам маркер")]
        public ДействиеЭкипажа Action { get; set; }
        [Editable, Serialize(КтоИсполняет.ВсеПассажиры, IsPropertySaveable.Yes, description: "Все пассажиры по очереди или одно место")]
        public КтоИсполняет Who { get; set; }
        [Editable(0, 15), Serialize(0, IsPropertySaveable.Yes, description: "Номер места для «Место»")]
        public int Seat { get; set; }
        [Editable(0f, 3600f, 1), Serialize(0f, IsPropertySaveable.Yes, description: "Начало от начала кадра, секунды")]
        public float StartAt { get; set; }
        [Editable(0f, 60f, 1), Serialize(1.5f, IsPropertySaveable.Yes, description: "Интервал между пассажирами, секунды")]
        public float Interval { get; set; }
        [Editable(0.1f, 10f, 1), Serialize(1.5f, IsPropertySaveable.Yes, description: "Скорость ходьбы, м/с (больше 2 — бег)")]
        public new float Speed { get; set; }
        [Editable(0.05f, 20f, 2), Serialize(1f, IsPropertySaveable.Yes, description: "Радиус достижения цели, м")]
        public float Radius { get; set; }
        [Editable(0.1f, 600f, 1), Serialize(30f, IsPropertySaveable.Yes, description: "Предельное время перемещения, секунды")]
        public float Timeout { get; set; }
        [Editable(0f, 1000f, 0), Serialize(80f, IsPropertySaveable.Yes, description: "Расстояние между пассажирами в строю, единицы подлодки")]
        public float Spacing { get; set; }
        [Editable, Serialize(ПозаЭкипажа.Стоя, IsPropertySaveable.Yes, description: "Поза для действия «Поза»")]
        public ПозаЭкипажа Pose { get; set; }
        protected override void Fill(CinematicMarkerRecord record) => record.Actor = new CinematicActorSettings {
            Action = (CinematicActorKind)Action, Who = (CinematicActorGroup)Who, Seat = Seat, StartAt = StartAt, Interval = Interval, Speed = Speed, Radius = Radius,
            Timeout = Timeout, Spacing = Spacing, Pose = (CinematicPoseKind)Pose
        };
    }

    public sealed class ScpCineEffect : ScpCineMarker {
        public ScpCineEffect(Item item, ContentXElement element) : base(item, element, CinematicMarkerKind.Effect) { }
        [Editable, Serialize(ВидЭффекта.Звук, IsPropertySaveable.Yes, description: "Вид эффекта")]
        public ВидЭффекта Effect { get; set; }
        [Editable(MaxLength = 256), Serialize("", IsPropertySaveable.Yes, description: "Путь к звуку внутри мода или идентификатор частиц")]
        public string Resource { get; set; }
        [Editable(0f, 3600f, 1), Serialize(0f, IsPropertySaveable.Yes, description: "Начало от начала кадра, секунды")]
        public float StartAt { get; set; }
        [Editable(0.1f, 3600f, 1), Serialize(5f, IsPropertySaveable.Yes, description: "Длительность, секунды")]
        public float Duration { get; set; }
        [Editable(0f, 10f, 2), Serialize(1f, IsPropertySaveable.Yes, description: "Громкость, яркость или интенсивность")]
        public float Intensity { get; set; }
        [Editable(0.05f, 1000f, 1), Serialize(30f, IsPropertySaveable.Yes, description: "Дальность звука или радиус зоны, м")]
        public float Range { get; set; }
        [Editable(-360f, 360f, 0), Serialize(90f, IsPropertySaveable.Yes, description: "Направление выброса частиц, градусы (90 — вверх)")]
        public float Direction { get; set; }
        [Editable(0f, 300f, 1), Serialize(0f, IsPropertySaveable.Yes, description: "Скорость выброса частиц, м/с")]
        public new float Speed { get; set; }
        [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Звук по кругу")]
        public bool Loop { get; set; }
        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Свет включить или выключить")]
        public bool Enabled { get; set; }
        [Editable(MaxLength = 64), Serialize("", IsPropertySaveable.Yes, description: "Имя сигнала для эффекта «Сигнал»")]
        public string Signal { get; set; }
        [Editable(MaxLength = 200), Serialize("", IsPropertySaveable.Yes, description: "Текст титра; перенос строки — символ |")]
        public string Text { get; set; }
        [Editable(MaxLength = 64), Serialize("", IsPropertySaveable.Yes, description: "Пройденная точка ухода для удаления; дальность — минимум до зрителей, Fade — затухание")]
        public string RemovalWaypoint { get; set; }
        [Editable(0.01f, 30f, 2), Serialize(1f, IsPropertySaveable.Yes, description: "Появление и исчезновение титра, секунды")]
        public float Fade { get; set; }
        [Editable(0.1f, 0.9f, 2), Serialize(0.5f, IsPropertySaveable.Yes, description: "Высота титра на экране: 0.1 — верх, 0.9 — низ")]
        public float Place { get; set; }
        protected override void Fill(CinematicMarkerRecord record) => record.Effect = new CinematicEffectSettings {
            Effect = (CinematicEffectKind)Effect, Resource = Resource ?? "", StartAt = StartAt, Duration = Duration, Intensity = Intensity, Range = Range,
            Direction = Direction, Speed = Speed, Loop = Loop, Enabled = Enabled, Signal = Signal ?? "", Text = Text ?? "", RemovalWaypoint = RemovalWaypoint ?? "", Fade = Fade, Place = Place
        };
    }
}
