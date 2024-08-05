using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Core.Chart;
using YARG.Core.Logging;
using YARG.Core.Utility;

namespace YARG.Core.Engine
{
    public abstract class BaseEngine<TNoteType, TEngineParams, TEngineStats, TEngineState> : BaseEngine
        where TNoteType : Note<TNoteType>
        where TEngineParams : BaseEngineParameters
        where TEngineStats : BaseStats, new()
        where TEngineState : BaseEngineState, new()
    {
        // Max number of measures that SP will last when draining
        // SP draining is done based on measures
        protected const double STAR_POWER_MEASURE_AMOUNT = 1.0 / STAR_POWER_MAX_MEASURES;

        // Max number of beats that it takes to fill SP when gaining
        // SP gain from whammying is done based on beats
        protected const double STAR_POWER_BEAT_AMOUNT = 1.0 / STAR_POWER_MAX_BEATS;

        // Number of measures that SP phrases will grant when hit
        protected const int    STAR_POWER_PHRASE_MEASURE_COUNT = 2;
        protected const double STAR_POWER_PHRASE_AMOUNT = STAR_POWER_PHRASE_MEASURE_COUNT * STAR_POWER_MEASURE_AMOUNT;

        public delegate void NoteHitEvent(int noteIndex, TNoteType note);

        public delegate void NoteMissedEvent(int noteIndex, TNoteType note);

        public delegate void StarPowerPhraseHitEvent(TNoteType note);

        public delegate void StarPowerPhraseMissEvent(TNoteType note);

        public delegate void SustainStartEvent(TNoteType note);

        public delegate void SustainEndEvent(TNoteType note, double timeEnded, bool finished);

        public delegate void CountdownChangeEvent(int measuresLeft, double countdownLength, double endTime);

        public NoteHitEvent?    OnNoteHit;
        public NoteMissedEvent? OnNoteMissed;

        public StarPowerPhraseHitEvent?  OnStarPowerPhraseHit;
        public StarPowerPhraseMissEvent? OnStarPowerPhraseMissed;

        public SustainStartEvent? OnSustainStart;
        public SustainEndEvent?   OnSustainEnd;

        public CountdownChangeEvent? OnCountdownChange;

        protected SustainList<TNoteType> ActiveSustains = new(10);

        protected          int[]  StarScoreThresholds { get; }
        protected readonly double TicksPerSustainPoint;
        protected readonly uint   SustainBurstThreshold;

        public readonly TEngineStats EngineStats;

        protected readonly InstrumentDifficulty<TNoteType> Chart;

        protected readonly List<TNoteType> Notes;
        protected readonly TEngineParams   EngineParameters;

        public TEngineState State;

        public override BaseEngineState      BaseState      => State;
        public override BaseEngineParameters BaseParameters => EngineParameters;
        public override BaseStats            BaseStats      => EngineStats;

        protected BaseEngine(InstrumentDifficulty<TNoteType> chart, SyncTrack syncTrack,
            TEngineParams engineParameters, bool isChordSeparate, bool isBot)
            : base(syncTrack, isChordSeparate, isBot)
        {
            Chart = chart;
            Notes = Chart.Notes;
            EngineParameters = engineParameters;

            EngineStats = new TEngineStats();
            State = new TEngineState();
            State.Reset();

            EngineStats.ScoreMultiplier = 1;
            if (TreatChordAsSeparate)
            {
                foreach (var note in Notes)
                {
                    EngineStats.TotalNotes += GetNumberOfNotes(note);
                }
            }
            else
            {
                EngineStats.TotalNotes = Notes.Count;
            }

            EngineStats.TotalStarPowerPhrases = Chart.Phrases.Count((phrase) => phrase.Type == PhraseType.StarPower);

            TicksPerSustainPoint = Resolution / (double) POINTS_PER_BEAT;
            SustainBurstThreshold = Resolution / SUSTAIN_BURST_FRACTION;

            // This method should only rely on the `Notes` property (which is assigned above).
            // ReSharper disable once VirtualMemberCallInConstructor
            BaseScore = CalculateBaseScore();

            float[] multiplierThresholds = engineParameters.StarMultiplierThresholds;
            StarScoreThresholds = new int[multiplierThresholds.Length];
            for (int i = 0; i < multiplierThresholds.Length; i++)
            {
                StarScoreThresholds[i] = (int) (BaseScore * multiplierThresholds[i]);
            }

            Solos = GetSoloSections();

            WaitCountdowns = GetWaitCountdowns();
        }

        protected override void GenerateQueuedUpdates(double nextTime)
        {
            base.GenerateQueuedUpdates(nextTime);
            var previousTime = State.CurrentTime;

            for (int i = State.NoteIndex; i < Notes.Count; i++)
            {
                var note = Notes[i];

                var hitWindow = EngineParameters.HitWindow.CalculateHitWindow(GetAverageNoteDistance(note));

                var noteFrontEnd = note.Time + EngineParameters.HitWindow.GetFrontEnd(hitWindow);
                var noteBackEnd = note.Time + EngineParameters.HitWindow.GetBackEnd(hitWindow);

                // Note will not reach front end yet
                if (nextTime < noteFrontEnd)
                {
                    //YargLogger.LogFormatTrace("Note {0} front end will not be reached at {1}", i, nextTime);
                    break;
                }

                if (!IsBot)
                {
                    // Earliest the note can be hit
                    if (IsTimeBetween(noteFrontEnd, previousTime, nextTime))
                    {
                        YargLogger.LogFormatTrace("Queuing note {0} front end hit time at {1}", i, noteFrontEnd);
                        QueueUpdateTime(noteFrontEnd, "Note Front End");
                    }
                }
                else
                {
                    if (IsTimeBetween(note.Time, previousTime, nextTime))
                    {
                        YargLogger.LogFormatTrace("Queuing bot note {0} at {1}", i, note.Time);
                        QueueUpdateTime(note.Time, "Bot Note Time");
                    }
                }

                // Note will not be out of time on the exact back end
                // So we increment the back end by 1 bit exactly
                // (essentially just 1 epsilon bigger)
                var noteBackEndIncrement = MathUtil.BitIncrement(noteBackEnd);

                if (IsTimeBetween(noteBackEndIncrement, previousTime, nextTime))
                {
                    YargLogger.LogFormatTrace("Queuing note {0} back end miss time at {1}", i, noteBackEndIncrement);
                    QueueUpdateTime(noteBackEndIncrement, "Note Back End");
                }
            }

            if (State.CurrentWaitCountdownIndex < WaitCountdowns.Count)
            {
                // Queue updates for countdown start/end/change

                if (State.IsWaitCountdownActive)
                {
                    var currentCountdown = WaitCountdowns[State.CurrentWaitCountdownIndex];
                    double deactivateTime = currentCountdown.DeactivateTime;

                    if (IsTimeBetween(deactivateTime, previousTime, nextTime))
                    {
                        YargLogger.LogFormatTrace("Queuing countdown {0} deactivation at {1}", State.CurrentWaitCountdownIndex, deactivateTime);
                        QueueUpdateTime(deactivateTime, "Deactivate Countdown");
                    }
                }
                else
                {
                    int nextCountdownIndex;

                    if (previousTime < WaitCountdowns[State.CurrentWaitCountdownIndex].Time)
                    {
                        // No countdowns are currently displayed
                        // CurrentWaitCountdownIndex has already been incremented for the next countdown
                        nextCountdownIndex = State.CurrentWaitCountdownIndex;
                    }
                    else
                    {
                        // A countdown is currently onscreen, but is past its deactivation time and is fading out
                        // CurrentWaitCountdownIndex will not be incremented until the progress bar no longer needs updating
                        nextCountdownIndex = State.CurrentWaitCountdownIndex + 1;
                    }

                    if (nextCountdownIndex < WaitCountdowns.Count)
                    {
                        double nextCountdownStartTime = WaitCountdowns[nextCountdownIndex].Time;

                        if (IsTimeBetween(nextCountdownStartTime, previousTime, nextTime))
                        {
                            YargLogger.LogFormatTrace("Queuing countdown {0} start time at {1}", nextCountdownIndex, nextCountdownStartTime);
                            QueueUpdateTime(nextCountdownStartTime, "Activate Countdown");
                        }
                    }
                }
            }
        }

        protected override void UpdateTimeVariables(double time)
        {
            if (time < State.CurrentTime)
            {
                YargLogger.FailFormat("Time cannot go backwards! Current time: {0}, new time: {1}", State.CurrentTime,
                    time);
            }

            State.LastUpdateTime = State.CurrentTime;
            State.LastTick = State.CurrentTick;

            State.CurrentTime = time;
            State.CurrentTick = GetCurrentTick(time);

            while (NextSyncIndex < SyncTrackChanges.Count && State.CurrentTick >= SyncTrackChanges[NextSyncIndex].Tick)
            {
                CurrentSyncIndex++;
            }

            // Only check for WaitCountdowns in this chart if there are any remaining
            if (State.CurrentWaitCountdownIndex < WaitCountdowns.Count)
            {
                var currentCountdown = WaitCountdowns[State.CurrentWaitCountdownIndex];

                if (time >= currentCountdown.Time)
                {
                    if (!State.IsWaitCountdownActive && time < currentCountdown.DeactivateTime)
                    {
                        // Entered new countdown window
                        State.IsWaitCountdownActive = true;
                        YargLogger.LogFormatTrace("Countdown {0} activated at time {1}. Expected time: {2}", State.CurrentWaitCountdownIndex, time, currentCountdown.Time);
                    }

                    if (time <= currentCountdown.DeactivateTime + WaitCountdown.FADE_ANIM_LENGTH)
                    {
                        // This countdown is currently displayed onscreen
                        int newMeasuresLeft = currentCountdown.CalculateMeasuresLeft(State.CurrentTick);

                        if (State.IsWaitCountdownActive && !currentCountdown.IsActive)
                        {
                            State.IsWaitCountdownActive = false;
                            YargLogger.LogFormatTrace("Countdown {0} deactivated at time {1}. Expected time: {2}", State.CurrentWaitCountdownIndex, time, currentCountdown.DeactivateTime);
                        }

                        UpdateCountdown(newMeasuresLeft, currentCountdown.TimeLength, currentCountdown.TimeEnd);
                    }
                    else
                    {
                        State.CurrentWaitCountdownIndex++;
                    }
                }
            }
        }

        public override void AllowStarPower(bool isAllowed)
        {
            if (isAllowed == State.AllowStarPower)
            {
                return;
            }

            State.AllowStarPower = isAllowed;

            foreach (var note in Notes)
            {
                if (isAllowed)
                {
                    note.ResetFlags();
                }
                else if (note.IsStarPower)
                {
                    note.Flags &= ~NoteFlags.StarPower;
                    foreach (var childNote in note.ChildNotes)
                    {
                        childNote.Flags &= ~NoteFlags.StarPower;
                    }
                }
            }
        }

        public override void Reset(bool keepCurrentButtons = false)
        {
            InputQueue.Clear();

            State.Reset();
            EngineStats.Reset();

            EventLogger.Clear();

            foreach (var note in Notes)
            {
                note.ResetNoteState();
            }

            foreach (var solo in Solos)
            {
                solo.NotesHit = 0;
                solo.SoloBonus = 0;
            }
        }

        protected abstract void CheckForNoteHit();

        /// <summary>
        /// Checks if the given note can be hit with the current input state.
        /// </summary>
        /// <param name="note">The Note to attempt to hit.</param>
        /// <returns>True if note can be hit. False otherwise.</returns>
        protected abstract bool CanNoteBeHit(TNoteType note);

        protected abstract bool CanSustainHold(TNoteType note);

        protected virtual void HitNote(TNoteType note)
        {
            if (note.ParentOrSelf.WasFullyHitOrMissed())
            {
                AdvanceToNextNote(note);
            }
        }

        protected virtual void MissNote(TNoteType note)
        {
            if (note.ParentOrSelf.WasFullyHitOrMissed())
            {
                AdvanceToNextNote(note);
            }
        }

        protected bool SkipPreviousNotes(TNoteType current)
        {
            bool skipped = false;
            var prevNote = current.PreviousNote;
            while (prevNote is not null && !prevNote.WasFullyHitOrMissed())
            {
                skipped = true;
                YargLogger.LogFormatTrace("Missed note (Index: {0}) ({1}) due to note skip at {2}", State.NoteIndex, prevNote.IsParent ? "Parent" : "Child", State.CurrentTime);
                MissNote(prevNote);

                if (TreatChordAsSeparate)
                {
                    foreach (var child in prevNote.ChildNotes)
                    {
                        YargLogger.LogFormatTrace("Missed note (Index: {0}) ({1}) due to note skip at {2}", State.NoteIndex, child.IsParent ? "Parent" : "Child", State.CurrentTime);
                        MissNote(child);
                    }
                }

                prevNote = prevNote.PreviousNote;
            }

            return skipped;
        }

        protected abstract void AddScore(TNoteType note);

        protected void AddScore(int score)
        {
            int multiplierScore = score * EngineStats.ScoreMultiplier;
            EngineStats.CommittedScore += multiplierScore;

            if (EngineStats.IsStarPowerActive)
            {
                // Amount of points just from Star Power is half of the current multiplier (8x total -> 4x SP points)
                EngineStats.StarPowerScore += multiplierScore / 2;
            }
            UpdateStars();
        }

        protected virtual void UpdateSustains()
        {
            EngineStats.PendingScore = 0;

            for (int i = 0; i < ActiveSustains.Count; i++)
            {
                ref var sustain = ref ActiveSustains[i];
                var note = sustain.Note;

                // If we're close enough to the end of the sustain, finish it
                // Provides leniency for sustains with no gap (and just in general)
                bool isBurst;

                // Sustain is too short for a burst
                if (SustainBurstThreshold > note.TickLength)
                {
                    isBurst = State.CurrentTick >= note.Tick;
                }
                else
                {
                    isBurst = State.CurrentTick >= note.TickEnd - SustainBurstThreshold;
                }

                bool isEndOfSustain = State.CurrentTick >= note.TickEnd;

                uint sustainTick = isBurst || isEndOfSustain ? note.TickEnd : State.CurrentTick;

                bool dropped = !CanSustainHold(note);

                // If the sustain has not finished scoring, then we need to calculate the points
                if (!sustain.HasFinishedScoring)
                {
                    // Sustain has reached burst threshold, so all points have been given
                    if (isBurst || isEndOfSustain)
                    {
                        sustain.HasFinishedScoring = true;
                    }

                    // Sustain has ended, so commit the points
                    if (dropped || isBurst || isEndOfSustain)
                    {
                        YargLogger.LogFormatTrace("Finished scoring sustain ({0}) at {1} (dropped: {2}, burst: {3})",
                            sustain.Note.Tick, State.CurrentTime, dropped, isBurst);

                        double finalScore = CalculateSustainPoints(ref sustain, sustainTick);
                        var points = (int) Math.Ceiling(finalScore);

                        AddScore(points);

                        // SustainPoints must include the multiplier, but NOT the star power multiplier
                        int sustainPoints = points * EngineStats.ScoreMultiplier;
                        if (EngineStats.IsStarPowerActive)
                        {
                            sustainPoints /= 2;
                        }

                        EngineStats.SustainScore += sustainPoints;
                    }
                    else
                    {
                        double score = CalculateSustainPoints(ref sustain, sustainTick);

                        var sustainPoints = (int) Math.Ceiling(score);

                        // It's ok to use multiplier here because PendingScore is only temporary to show the correct
                        // score on the UI.
                        EngineStats.PendingScore += sustainPoints * EngineStats.ScoreMultiplier;
                    }
                }

                // Only remove the sustain if its dropped or has reached the final tick
                if (dropped || isEndOfSustain)
                {
                    EndSustain(i, dropped, isEndOfSustain);
                    i--;
                }
            }

            UpdateStars();
        }

        protected virtual void StartSustain(TNoteType note)
        {
            var sustain = new ActiveSustain<TNoteType>(note);

            ActiveSustains.Add(sustain);

            YargLogger.LogFormatTrace("Started sustain at {0} (tick len: {1}, time len: {2})", State.CurrentTime, note.TickLength, note.TimeLength);

            OnSustainStart?.Invoke(note);
        }

        protected virtual void EndSustain(int sustainIndex, bool dropped, bool isEndOfSustain)
        {
            var sustain = ActiveSustains[sustainIndex];
            YargLogger.LogFormatTrace("Ended sustain ({0}) at {1} (dropped: {2}, end: {3})", sustain.Note.Tick, State.CurrentTime, dropped, isEndOfSustain);
            ActiveSustains.RemoveAt(sustainIndex);

            OnSustainEnd?.Invoke(sustain.Note, State.CurrentTime, sustain.HasFinishedScoring);
        }

        protected void UpdateStars()
        {
            // Update which star we're on
            while (State.CurrentStarIndex < StarScoreThresholds.Length &&
                EngineStats.StarScore > StarScoreThresholds[State.CurrentStarIndex])
            {
                State.CurrentStarIndex++;
            }

            // Calculate current star progress
            float progress = 0f;
            if (State.CurrentStarIndex < StarScoreThresholds.Length)
            {
                int previousPoints = State.CurrentStarIndex > 0 ? StarScoreThresholds[State.CurrentStarIndex - 1] : 0;
                int nextPoints = StarScoreThresholds[State.CurrentStarIndex];
                progress = YargMath.InverseLerpF(previousPoints, nextPoints, EngineStats.StarScore);
            }

            EngineStats.Stars = State.CurrentStarIndex + progress;
        }

        protected virtual void StripStarPower(TNoteType? note)
        {
            if (note is null || !note.IsStarPower)
            {
                return;
            }

            // Strip star power from the note and all its children
            note.Flags &= ~NoteFlags.StarPower;
            foreach (var childNote in note.ChildNotes)
            {
                childNote.Flags &= ~NoteFlags.StarPower;
            }

            // Look back until finding the start of the phrase
            if (!note.IsStarPowerStart)
            {
                var prevNote = note.PreviousNote;
                while (prevNote is not null && prevNote.IsStarPower)
                {
                    prevNote.Flags &= ~NoteFlags.StarPower;
                    foreach (var childNote in prevNote.ChildNotes)
                    {
                        childNote.Flags &= ~NoteFlags.StarPower;
                    }

                    if (prevNote.IsStarPowerStart)
                    {
                        break;
                    }

                    prevNote = prevNote.PreviousNote;
                }
            }

            // Look forward until finding the end of the phrase
            if (!note.IsStarPowerEnd)
            {
                var nextNote = note.NextNote;
                while (nextNote is not null && nextNote.IsStarPower)
                {
                    nextNote.Flags &= ~NoteFlags.StarPower;
                    foreach (var childNote in nextNote.ChildNotes)
                    {
                        childNote.Flags &= ~NoteFlags.StarPower;
                    }

                    if (nextNote.IsStarPowerEnd)
                    {
                        break;
                    }

                    nextNote = nextNote.NextNote;
                }
            }

            OnStarPowerPhraseMissed?.Invoke(note);
        }

        protected virtual uint CalculateStarPowerGain(uint tick) => tick - State.LastTick;

        protected void AwardStarPower(TNoteType note)
        {
            GainStarPower(TicksPerQuarterSpBar);

            OnStarPowerPhraseHit?.Invoke(note);
        }

        protected void StartSolo()
        {
            if (State.CurrentSoloIndex >= Solos.Count)
            {
                return;
            }

            State.IsSoloActive = true;
            OnSoloStart?.Invoke(Solos[State.CurrentSoloIndex]);
        }

        protected void EndSolo()
        {
            if (!State.IsSoloActive)
            {
                return;
            }

            var currentSolo = Solos[State.CurrentSoloIndex];

            double soloPercentage = currentSolo.NotesHit / (double) currentSolo.NoteCount;

            if (soloPercentage < 0.6)
            {
                currentSolo.SoloBonus = 0;
            }
            else
            {
                double multiplier = Math.Clamp((soloPercentage - 0.6) / 0.4, 0, 1);

                // Old engine says this is 200 *, but I'm not sure that's right?? Isn't it 2x the note's worth, not 4x?
                double points = 100 * currentSolo.NotesHit * multiplier;

                // Round down to nearest 50 (kinda just makes sense I think?)
                points -= points % 50;

                currentSolo.SoloBonus = (int) points;
            }

            EngineStats.SoloBonuses += currentSolo.SoloBonus;

            State.IsSoloActive = false;

            OnSoloEnd?.Invoke(Solos[State.CurrentSoloIndex]);
            State.CurrentSoloIndex++;
        }

        protected override void UpdateProgressValues(uint tick)
        {
            base.UpdateProgressValues(tick);

            EngineStats.PendingScore = 0;
            for (int i = 0; i < ActiveSustains.Count; i++)
            {
                ref var sustain = ref ActiveSustains[i];
                EngineStats.PendingScore += (int) CalculateSustainPoints(ref sustain, tick);
            }
        }

        protected override void RebaseProgressValues(uint baseTick)
        {
            base.RebaseProgressValues(baseTick);
            RebaseSustains(baseTick);
        }

        protected void RebaseSustains(uint baseTick)
        {
            EngineStats.PendingScore = 0;
            for (int i = 0; i < ActiveSustains.Count; i++)
            {
                ref var sustain = ref ActiveSustains[i];
                // Don't rebase sustains that haven't started yet
                if (baseTick < sustain.BaseTick)
                {
                    YargLogger.AssertFormat(baseTick < sustain.Note.Tick,
                        "Sustain base tick cannot go backwards! Attempted to go from {0} to {1}",
                        sustain.BaseTick, baseTick);

                    continue;
                }

                double sustainScore = CalculateSustainPoints(ref sustain, baseTick);

                sustain.BaseTick = Math.Clamp(baseTick, sustain.Note.Tick, sustain.Note.TickEnd);
                sustain.BaseScore = sustainScore;
                EngineStats.PendingScore += (int) sustainScore;
            }
        }

        protected void UpdateCountdown(int measuresLeft, double countdownLength, double endTime)
        {
            OnCountdownChange?.Invoke(measuresLeft, countdownLength, endTime);
        }

        public sealed override (double FrontEnd, double BackEnd) CalculateHitWindow()
        {
            var maxWindow = EngineParameters.HitWindow.MaxWindow;

            if (State.NoteIndex >= Notes.Count)
            {
                return (EngineParameters.HitWindow.GetFrontEnd(maxWindow),
                    EngineParameters.HitWindow.GetBackEnd(maxWindow));
            }

            var noteDistance = GetAverageNoteDistance(Notes[State.NoteIndex]);
            var hitWindow = EngineParameters.HitWindow.CalculateHitWindow(noteDistance);

            return (EngineParameters.HitWindow.GetFrontEnd(hitWindow),
                EngineParameters.HitWindow.GetBackEnd(hitWindow));
        }

        /// <summary>
        /// Calculates the base score of the chart, which can be used to calculate star thresholds.
        /// </summary>
        /// <remarks>
        /// Please be mindful that this virtual method is called in the constructor of
        /// <see cref="BaseEngine{TNoteType,TEngineParams,TEngineStats,TEngineState}"/>.
        /// <b>ONLY</b> use the <see cref="Notes"/> property to calculate this.
        /// </remarks>
        protected abstract int CalculateBaseScore();

        protected bool IsNoteInWindow(TNoteType note) => IsNoteInWindow(note, out _);

        protected bool IsNoteInWindow(TNoteType note, double time) =>
            IsNoteInWindow(note, out _, time);

        protected bool IsNoteInWindow(TNoteType note, out bool missed) =>
            IsNoteInWindow(note, out missed, State.CurrentTime);

        protected bool IsNoteInWindow(TNoteType note, out bool missed, double time)
        {
            missed = false;

            double hitWindow = EngineParameters.HitWindow.CalculateHitWindow(GetAverageNoteDistance(note));
            double frontend = EngineParameters.HitWindow.GetFrontEnd(hitWindow);
            double backend = EngineParameters.HitWindow.GetBackEnd(hitWindow);

            // Time has not reached the front end of this note
            if (time < note.Time + frontend)
            {
                return false;
            }

            // Time has surpassed the back end of this note
            if (time > note.Time + backend)
            {
                missed = true;
                return false;
            }

            return true;
        }

        protected double CalculateSustainPoints(ref ActiveSustain<TNoteType> sustain, uint tick)
        {
            uint scoreTick = Math.Clamp(tick, sustain.Note.Tick, sustain.Note.TickEnd);

            sustain.Note.SustainTicksHeld = scoreTick - sustain.Note.Tick;

            // Sustain points are awarded at a constant rate regardless of tempo
            // double deltaScore = CalculateBeatProgress(scoreTick, sustain.BaseTick, POINTS_PER_BEAT);
            double deltaScore = (scoreTick - sustain.BaseTick) / TicksPerSustainPoint;
            return sustain.BaseScore + deltaScore;
        }

        private void AdvanceToNextNote(TNoteType note)
        {
            State.NoteIndex++;
            ReRunHitLogic = true;
        }

        public double GetAverageNoteDistance(TNoteType note)
        {
            double previousToCurrent;
            double currentToNext = EngineParameters.HitWindow.MaxWindow / 2;

            if (note.NextNote is not null)
            {
                currentToNext = (note.NextNote.Time - note.Time) / 2;
            }

            if (note.PreviousNote is not null)
            {
                previousToCurrent = (note.Time - note.PreviousNote.Time) / 2;
            }
            else
            {
                previousToCurrent = currentToNext;
            }

            return previousToCurrent + currentToNext;
        }

        private List<SoloSection> GetSoloSections()
        {
            var soloSections = new List<SoloSection>();
            for (int i = 0; i < Notes.Count; i++)
            {
                var start = Notes[i];
                if (!start.IsSoloStart)
                {
                    continue;
                }

                // note is a SoloStart

                // Try to find a solo end
                int soloNoteCount = GetNumberOfNotes(start);
                for (int j = i + 1; j < Notes.Count; j++)
                {
                    var end = Notes[j];

                    soloNoteCount += GetNumberOfNotes(end);

                    if (!end.IsSoloEnd) continue;

                    soloSections.Add(new SoloSection(soloNoteCount));

                    // Move i to the end of the solo section
                    i = j;
                    break;
                }
            }

            return soloSections;
        }

        private List<WaitCountdown> GetWaitCountdowns()
        {
            var allMeasureBeatLines = SyncTrack.Beatlines.Where(x => x.Type == BeatlineType.Measure).ToList();

            var waitCountdowns = new List<WaitCountdown>();
            for (int i = 0; i < Notes.Count; i++)
            {
                // Compare the note at the current index against the previous note
                // Create a countdown if the distance between the notes is > 10s
                Note<TNoteType> noteOne;

                uint noteOneTickEnd = 0;
                double noteOneTimeEnd = 0;

                if (i > 0) {
                    noteOne = Notes[i-1];
                    noteOneTickEnd = noteOne.TickEnd;
                    noteOneTimeEnd = noteOne.TimeEnd;
                }

                Note<TNoteType> noteTwo = Notes[i];
                double noteTwoTime = noteTwo.Time;

                if (noteTwoTime - noteOneTimeEnd >= WaitCountdown.MIN_SECONDS)
                {
                    uint noteTwoTick = noteTwo.Tick;

                    // Determine the total number of measures that will pass during this countdown
                    List<Beatline> beatlinesThisCountdown = new();

                    // Countdown should start at end of the first note if it's directly on a measure line
                    // Otherwise it should start at the beginning of the next measure
                    int curMeasureIndex = allMeasureBeatLines.GetIndexOfPrevious(noteOneTickEnd);
                    if (allMeasureBeatLines[curMeasureIndex].Tick < noteOneTickEnd) curMeasureIndex++;

                    var curMeasureline = allMeasureBeatLines[curMeasureIndex];
                    while (curMeasureline.Tick <= noteTwoTick)
                    {
                        // Skip counting on measures that are too close together
                        if (beatlinesThisCountdown.Count == 0 ||
                            curMeasureline.Time - beatlinesThisCountdown.Last().Time >= WaitCountdown.MIN_MEASURE_LENGTH)
                        {
                            beatlinesThisCountdown.Add(curMeasureline);
                        }

                        curMeasureIndex++;

                        if (curMeasureIndex >= allMeasureBeatLines.Count)
                        {
                            break;
                        }

                        curMeasureline = allMeasureBeatLines[curMeasureIndex];
                    }

                    // Prevent showing countdowns < 4 measures at low BPMs
                    int countdownTotalMeasures = beatlinesThisCountdown.Count;
                    if (countdownTotalMeasures >= WaitCountdown.MIN_MEASURES)
                    {
                        // Create a WaitCountdown instance to reference at runtime
                        var newCountdown = new WaitCountdown(beatlinesThisCountdown);

                        waitCountdowns.Add(newCountdown);
                        YargLogger.LogFormatTrace("Created a WaitCountdown at time {0} of {1} measures and {2} seconds in length",
                                                 newCountdown.Time, countdownTotalMeasures, beatlinesThisCountdown[^1].Time - noteOneTimeEnd);
                    }
                    else
                    {
                        YargLogger.LogFormatTrace("Did not create a WaitCountdown at time {0} of {1} seconds in length because it was only {2} measures long",
                                                 noteOneTimeEnd, beatlinesThisCountdown[^1].Time - noteOneTimeEnd, countdownTotalMeasures);
                    }
                }
            }

            return waitCountdowns;
        }
    }
}