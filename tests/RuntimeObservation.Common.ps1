# Pure observation-gap accounting, shared with the sampler's synthetic tests.
function New-RuntimeObservationState([datetime]$StartedAtUtc,[double]$IntervalSeconds) {
    @{StartedAtUtc=$StartedAtUtc;LastSampleAtUtc=$null;SampleCount=0;
        GapThresholdSeconds=[Math]::Max(2,4*$IntervalSeconds);MaximumSampleIntervalSeconds=0.0;
        MaximumObservationGapSeconds=0.0;LongSampleGapCount=0;LongObservationGapCount=0;ClockRegressionCount=0}
}
function Add-RuntimeObservationSample($State,[datetime]$AtUtc) {
    $previous=$State.StartedAtUtc
    if($null -ne $State.LastSampleAtUtc){$previous=$State.LastSampleAtUtc}
    $gap=($AtUtc-$previous).TotalSeconds
    if($gap -lt 0){$State.ClockRegressionCount++}
    $State.MaximumObservationGapSeconds=[Math]::Max($State.MaximumObservationGapSeconds,$gap)
    if($gap -gt $State.GapThresholdSeconds){$State.LongObservationGapCount++}
    if($null -ne $State.LastSampleAtUtc){
        $State.MaximumSampleIntervalSeconds=[Math]::Max($State.MaximumSampleIntervalSeconds,$gap)
        if($gap -gt $State.GapThresholdSeconds){$State.LongSampleGapCount++}
    }
    $State.SampleCount++;$State.LastSampleAtUtc=$AtUtc
}
function Get-RuntimeObservationContinuity($State,[datetime]$AtUtc,[bool]$Completed,[int]$PartialTreeSampleCount=0) {
    $last=$State.StartedAtUtc;if($null -ne $State.LastSampleAtUtc){$last=$State.LastSampleAtUtc}
    $tail=($AtUtc-$last).TotalSeconds
    $longGaps=$State.LongObservationGapCount+[int]($tail -gt $State.GapThresholdSeconds)
    $clockRegressions=$State.ClockRegressionCount+[int]($tail -lt 0)
    $continuous=$Completed -and $State.SampleCount -ge 2 -and $longGaps -eq 0 -and $clockRegressions -eq 0
    $wallSeconds=[Math]::Max(0,($AtUtc-$State.StartedAtUtc).TotalSeconds)
    [pscustomobject]@{GapThresholdSeconds=$State.GapThresholdSeconds;MaximumSampleIntervalSeconds=$State.MaximumSampleIntervalSeconds;
        MaximumObservationGapSeconds=[Math]::Max($State.MaximumObservationGapSeconds,$tail);
        LongSampleGapCount=$State.LongSampleGapCount;LongObservationGapCount=$longGaps;ClockRegressionCount=$clockRegressions;
        TailObservationGapSeconds=[Math]::Max(0,$tail);ObservedWallDurationSeconds=$wallSeconds;
        ContinuousRequestedDurationObserved=$continuous;ContinuousEightHoursObserved=($continuous -and $wallSeconds -ge 28800);
        CompleteTreeEightHoursObserved=($continuous -and $wallSeconds -ge 28800 -and $PartialTreeSampleCount -eq 0);
        Boundary='A long gap is greater than max(4 * IntervalSeconds, 2 seconds). Observation gaps include initial delay and the report tail; no intervening process identity or resource use is inferred.'}
}
