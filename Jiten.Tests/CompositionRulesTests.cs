using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Xunit;

namespace Jiten.Tests;

public class CompositionRulesTests
{
    private static readonly PartOfSpeech[] IsaiWoHanatsu = [PartOfSpeech.Noun, PartOfSpeech.Particle, PartOfSpeech.Verb];

    [Fact]
    public void ExpressionParent_WithAParticle_IsKeptAsAnExpression()
    {
        CompositionRules.Classify(["exp", "v5t"], IsaiWoHanatsu).Should().Be(CompositionGate.Expression);
    }

    [Fact]
    public void ExpressionParent_WithAnAuxiliary_IsKeptAsAnExpression()
    {
        CompositionRules.Classify(["exp", "adj-i"], [PartOfSpeech.Verb, PartOfSpeech.Auxiliary])
                        .Should().Be(CompositionGate.Expression);
    }

    [Fact]
    public void NonExpressionParent_WithAParticle_IsAPhrase()
    {
        CompositionRules.Classify(["n"], IsaiWoHanatsu).Should().Be(CompositionGate.Phrase);
    }

    [Fact]
    public void ExpressionParent_MadeOnlyOfGrammar_IsAPhrase()
    {
        CompositionRules.Classify(["exp"], [PartOfSpeech.Particle, PartOfSpeech.Auxiliary])
                        .Should().Be(CompositionGate.Phrase);
    }

    [Fact]
    public void ContentOnlySplit_IsACompound_WhateverTheParent()
    {
        CompositionRules.Classify(["n"], [PartOfSpeech.Noun, PartOfSpeech.Noun]).Should().Be(CompositionGate.Compound);
        CompositionRules.Classify(["exp"], [PartOfSpeech.Noun, PartOfSpeech.Verb]).Should().Be(CompositionGate.Compound);
    }
}
