﻿namespace Linker.Core;

public static class Ensure
{
    public static void NotNull<T>(T argument, string argumentName) where T : class
    {
        if (argument == null)
            throw new ArgumentNullException(argumentName);
    }

    public static void NotNullOrEmpty(string argument, string argumentName)
    {
        if (string.IsNullOrEmpty(argument))
            throw new ArgumentNullException(argument, argumentName);
    }

    public static void NotNullOrWhiteSpace(string argument, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(argument))
            throw new ArgumentNullException(argument, argumentName);
    }

    public static void Positive(int number, string argumentName)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(argumentName, argumentName + " should be positive.");
    }

    public static void Nonnegative(decimal number, string argumentName)
    {
        if (number < 0)
            throw new ArgumentOutOfRangeException(argumentName, argumentName + " should be non negative.");
    }

    public static void Positive(long number, string argumentName)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(argumentName, argumentName + " should be positive.");
    }

    public static void Nonnegative(long number, string argumentName)
    {
        if (number < 0)
            throw new ArgumentOutOfRangeException(argumentName, argumentName + " should be non negative.");
    }

    public static void Nonnegative(int number, string argumentName)
    {
        if (number < 0)
            throw new ArgumentOutOfRangeException(argumentName, argumentName + " should be non negative.");
    }
    public static void NonLessThan50Years(DateTime date, string argumentName)
    {
        if ((date - DateTime.Now).TotalDays > 18250)
            throw new ArgumentOutOfRangeException(argumentName, argumentName + " should be a valid date");
    }

    public static void NotEmptyGuid(Guid guid, string argumentName)
    {
        if (Guid.Empty == guid)
            throw new ArgumentException(argumentName, argumentName + " should be non-empty GUID.");
    }

    public static void Equal(int expected, int actual, string argumentName)
    {
        if (expected != actual)
            throw new ArgumentException(string.Format("{0} expected value: {1}, actual value: {2}", argumentName, expected, actual));
    }

    public static void Equal(long expected, long actual, string argumentName)
    {
        if (expected != actual)
            throw new ArgumentException(string.Format("{0} expected value: {1}, actual value: {2}", argumentName, expected, actual));
    }

    public static void Equal(bool expected, bool actual, string argumentName)
    {
        if (expected != actual)
            throw new ArgumentException(string.Format("{0} expected value: {1}, actual value: {2}", argumentName, expected, actual));
    }
}