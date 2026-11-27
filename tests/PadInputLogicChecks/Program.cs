int checks = 0;
TriggerInputChecks.Run((condition, message) =>
{
    ++checks;
    if (!condition) throw new InvalidOperationException(message);
});
Console.WriteLine($"PASS: {checks} trigger input assertions (no controller or Windows required).");
