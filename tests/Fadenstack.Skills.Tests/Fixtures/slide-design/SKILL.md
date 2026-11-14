---
name: slide-design
description: How to make slides and decks that look designed, not typed. Use when making, changing or reviewing slides or a presentation, in PowerPoint or in a chat about a deck.
---

# Slide design

## One message per slide

- The title says the point as a short sentence: "Ten weeks to the first motor", not "Delivery". 12 words or fewer.
- Everything else on the slide supports that one point. A second point is a second slide.
- The audience listens while they look. If the slide must be read, it is a document: move the detail to the
  speaker notes.

## Pick the pattern from the content

| The content | Pattern |
|---|---|
| A figure that matters (money, time, quantity) | `big_numbers`: up to four, each with a short label and a one-line caption |
| 2-6 parallel points (products, reasons, risks, offers) | `cards`: a heading and one short sentence each |
| Dates, steps, a plan | `timeline`: when (heading) and what (text), 2-6 steps |
| Two or three options side by side | `comparison`: a heading and a few short lines each |
| One key message or decision | `statement` |
| The parts of a meeting or a deck | `agenda` |
| Next steps: what, who, by when | `actions`: the action as text, the person as heading, the date as value |
| A new part of the deck | `section` |
| A list that really is a list | `bullets`, at most 3-5 of a few words |

Bullets are the last choice, not the first.

## Write for the pattern

- **Cards and captions:** one short sentence, no full stop needed, no sub-bullets.
- **Numbers:** the figure alone in `value` ("€10,000", "10 weeks", "14"); what it means in `text` ("for 14
  servo axes"); a two-word label in `heading` if it helps.
- **Timelines:** the when as a date or a step ("End of Oct", "Week 10"), the what in a few words.
- **Notes:** the reasoning, the numbers behind the numbers, what to say. Put them in the same `add_slide` call.

## Use the deck's design

- The theme's fonts and colours only; the patterns already use them. One accent colour, used sparingly.
- No shadows, 3-D, gradients or clip art. No text in all caps beyond a short label.
- Left-aligned text; centred only for short labels on timelines and figures.
- White space is part of the design: do not fill the slide.

## Look before you answer

After each slide you make or change:

1. Look at it: `render_slide` when you can see pictures, else `check_slide`.
2. Fix what the check reports or what you see. Shorten the text, which is better than a smaller font. Move or
   resize a shape, or choose another pattern.
3. Look again after a fix. Answer only when the slide is clean, and say briefly what you made.

## A deck that tells a story

- **Situation, problem, answer:** what is the case, what is in the way, what we propose.
- **One idea per slide, in an order that builds:** the reader of the titles alone should get the story.
- **A sales update:** pipeline, the opportunities that need a decision (one slide each), risks, next steps.
- **A status report:** where we are (big numbers), what changed, what is late and why, what we need.

## Avoid

- Titles that are topics ("Overview", "Delivery", "Next steps") instead of points.
- More than about 60 words on a slide.
- Two slides that say the same thing.
- Changing the deck's theme, or other slides, unless asked.
- Adding facts when redesigning a slide: keep what it says, in fewer words and a better form.
